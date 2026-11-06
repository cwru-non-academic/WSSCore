using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using Wss.CalibrationModule;
using Wss.ModelModule;
using Wss.Transports;

namespace Wss.CoreModule
{
    /// <summary>Selects the layers constructed from a validated configuration snapshot.</summary>
    public enum WssConfigurationLayer
    {
        /// <summary>Constructs Core only.</summary>
        Core,
        /// <summary>Constructs Core and calibration.</summary>
        Calibration,
        /// <summary>Constructs Core, calibration and model.</summary>
        Model
    }

    /// <summary>Retains validated JSON values and anchored paths across host reconstruction.</summary>
    /// <remarks>Immutable and safe to retain across threads. Staging never writes files.
    /// Construction and application consume private copies, without rereading configuration files.</remarks>
    public sealed class WssConfigurationSnapshot
    {
        private static long nextGeneration;
        private readonly string coreJson, paramsJson, modelJson;
        /// <summary>Gets this snapshot's process-local identity.</summary>
        public long Generation { get; }
        /// <summary>Gets the selected Core file.</summary>
        public string CorePath { get; }
        /// <summary>Gets the selected calibration file.</summary>
        public string ParamsPath { get; }
        /// <summary>Gets the selected model file.</summary>
        public string ModelPath { get; }
        /// <summary>Gets the original Core default file, unaffected by later overrides.</summary>
        public string DefaultCorePath { get; }
        /// <summary>Gets the original calibration default file.</summary>
        public string DefaultParamsPath { get; }
        /// <summary>Gets the original model default file.</summary>
        public string DefaultModelPath { get; }

        private WssConfigurationSnapshot(ConfigurationBundle bundle, string coreDefault, string paramsDefault, string modelDefault)
        {
            CorePath = bundle.CorePath; ParamsPath = bundle.ParamsPath; ModelPath = bundle.ModelPath;
            DefaultCorePath = coreDefault; DefaultParamsPath = paramsDefault; DefaultModelPath = modelDefault;
            coreJson = bundle.Core.ToString(); paramsJson = bundle.Params.ToString(); modelJson = bundle.Model.ToString();
            Generation = Interlocked.Increment(ref nextGeneration);
        }

        /// <summary>Stages the three standard JSON files in a startup configuration directory.</summary>
        /// <param name="directory">Existing or intended directory, anchored to the current working directory once.</param>
        /// <returns>A deeply immutable snapshot, with the directory retained as startup defaults.</returns>
        /// <exception cref="InvalidDataException">A supplied file cannot be parsed or validated.</exception>
        public static WssConfigurationSnapshot ReadDefaults(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A configuration directory is required.", nameof(directory));
            string root = Path.GetFullPath(directory);
            string core = Path.Combine(root, "stimConfig.json"), parameters = Path.Combine(root, "stimParams.json"), model = Path.Combine(root, "modelConfig.json");
            return new WssConfigurationSnapshot(ConfigurationBundle.Read(core, parameters, model), core, parameters, model);
        }

        /// <summary>Stages all three startup-default files, regardless of the active profile.</summary>
        public WssConfigurationSnapshot ReloadDefaults() =>
            New(ConfigurationBundle.Read(DefaultCorePath, DefaultParamsPath, DefaultModelPath));

        /// <summary>Stages a directory profile or one explicit calibration file.</summary>
        /// <param name="path">Directory: all three standard filenames. File: calibration only, with startup-default Core and model.</param>
        /// <returns>A snapshot that preserves the original startup defaults.</returns>
        public WssConfigurationSnapshot Reload(string path)
        {
            string parameters = ConfigurationBundle.Resolve(path, "stimParams.json");
            bool directory = Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar.ToString()) || path.EndsWith(Path.AltDirectorySeparatorChar.ToString());
            return New(ConfigurationBundle.Read(directory ? Path.Combine(Path.GetDirectoryName(parameters), "stimConfig.json") : DefaultCorePath,
                parameters, directory ? Path.Combine(Path.GetDirectoryName(parameters), "modelConfig.json") : DefaultModelPath));
        }

        /// <summary>Stages edits to the active Core file while retaining the applied calibration/model values and paths.</summary>
        public WssConfigurationSnapshot ReloadCore() => New(ConfigurationBundle.Read(CorePath, ParamsPath, ModelPath,
            JObject.Parse(paramsJson), JObject.Parse(modelJson)));

        /// <summary>Compares all effective Core values, including normalized addresses and nested curves.</summary>
        public bool HasSameCore(WssConfigurationSnapshot other) => other != null && JToken.DeepEquals(JObject.Parse(coreJson), JObject.Parse(other.coreJson));

        /// <summary>Captures current values without reading files or retaining mutable controller references.</summary>
        /// <param name="stack">The active stack; calls are serialized with Core operations.</param>
        public WssConfigurationSnapshot CaptureCurrent(IStimulationCore stack)
        {
            if (stack == null) throw new ArgumentNullException(nameof(stack));
            var core = stack.GetCoreConfigController();
            lock (core.OperationGate)
            lock (core.ConfigurationGate)
            {
                var bundle = Materialize();
                bundle.CoreValues = core.CopyValues();
                ConfigurationBundle.NormalizeAddresses(bundle.CoreValues);
                foreach (var property in JObject.FromObject(bundle.CoreValues).Properties()) bundle.Core[property.Name] = property.Value.DeepClone();
                bundle.CorePath = core._configPath;
                if (stack is IStimParamsCore parameters)
                {
                    var controller = parameters.GetStimParamsConfigController();
                    bundle.Params = controller.CopyRoot(); bundle.ParamsPath = controller.ActivePath;
                }
                if (stack is IModelParamsCore model)
                {
                    var controller = model.GetModelConfigController();
                    bundle.Model = controller.CopyRoot(); bundle.ModelPath = controller.ActivePath;
                }
                return New(bundle);
            }
        }

        /// <summary>Constructs and begins initializing a stack from these values without configuration-file I/O.</summary>
        /// <param name="transport">New or reconnectable transport; ownership passes to the returned stack.</param>
        /// <param name="options">Initialization options. Snapshot paths take precedence over its ConfigPath.</param>
        /// <param name="layers">Layers required by the host.</param>
        /// <returns>A complete stack; initialization progresses through Tick. Partial construction is disposed on failure.</returns>
        public IStimulationCore BuildAndInitialize(ITransport transport, WssStimulationCoreOptions options, WssConfigurationLayer layers)
        {
            if (layers < WssConfigurationLayer.Core || layers > WssConfigurationLayer.Model)
                throw new ArgumentOutOfRangeException(nameof(layers));
            IStimulationCore owner = null;
            try
            {
                owner = new WssStimulationCore(transport, options, this);
                if (layers >= WssConfigurationLayer.Calibration) owner = new StimParamsLayer(owner, this);
                if (layers == WssConfigurationLayer.Model) owner = new ModelParamsLayer((IStimParamsCore)owner, this);
                if (owner is ModelParamsLayer model) model.InitializeInstalledConfiguration();
                else if (owner is StimParamsLayer parameters) parameters.InitializeInstalledConfiguration();
                else ((WssStimulationCore)owner).InitializeInstalledConfiguration();
                return owner;
            }
            catch
            {
                try { if (owner != null) owner.Dispose(); else transport?.Dispose(); } catch { }
                throw;
            }
        }

        /// <summary>Atomically installs values on an existing stack when its Core configuration is unchanged.</summary>
        /// <exception cref="InvalidOperationException">Core changed; the host must use its reset/reconstruction path.</exception>
        public void ApplyWithoutRestart(IStimulationCore stack)
        {
            if (stack == null) throw new ArgumentNullException(nameof(stack));
            var core = stack.GetCoreConfigController();
            lock (core.OperationGate)
            {
                if (!HasSameCore(CaptureCurrent(stack))) throw new InvalidOperationException("Core configuration changed; reset through the host before installation.");
                var bundle = Materialize();
                var concrete = stack as WssStimulationCore ?? (stack as StimParamsLayer)?.ConcreteCore ?? (stack as ModelParamsLayer)?.Calibration?.ConcreteCore;
                if (concrete == null) throw new NotSupportedException("Snapshot installation requires the Core layer stack.");
                concrete.PublishConfiguration(() =>
                {
                    lock (core.ConfigurationGate)
                    {
                        if (stack is ModelParamsLayer model) model.InstallSnapshot(bundle);
                        else
                        {
                            core.ApplySnapshot(bundle.CorePath, bundle.CoreValues);
                            if (stack is StimParamsLayer parameters)
                            {
                                parameters.GetStimParamsConfigController().ApplySnapshot(bundle.ParamsPath, bundle.Params, bundle.CoreValues.maxWSS);
                                parameters.RefreshChannels();
                            }
                        }
                    }
                });
            }
        }

        private WssConfigurationSnapshot New(ConfigurationBundle bundle) => new WssConfigurationSnapshot(bundle, DefaultCorePath, DefaultParamsPath, DefaultModelPath);
        private ConfigurationBundle Materialize() => new ConfigurationBundle
        {
            CorePath = CorePath, ParamsPath = ParamsPath, ModelPath = ModelPath,
            Core = JObject.Parse(coreJson), Params = JObject.Parse(paramsJson), Model = JObject.Parse(modelJson),
            CoreValues = JObject.Parse(coreJson).ToObject<CoreConfig>()
        };
        internal CoreConfigController CreateCoreController() => new CoreConfigController(CorePath, Materialize().CoreValues, DefaultCorePath);
        internal StimParamsConfigController CreateParamsController() => new StimParamsConfigController(ParamsPath, JObject.Parse(paramsJson), Materialize().CoreValues.maxWSS, DefaultParamsPath);
        internal ModelConfigController CreateModelController() => new ModelConfigController(ModelPath, JObject.Parse(modelJson), DefaultModelPath);
    }
}
