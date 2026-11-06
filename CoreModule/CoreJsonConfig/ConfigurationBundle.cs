using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Wss.CoreModule
{
    // A bundle owns parsed values, never live controller references. Staging does
    // not construct controllers (their constructors can create/backfill files).
    internal sealed class ConfigurationBundle
    {
        internal string CorePath, ParamsPath, ModelPath;
        internal JObject Core, Params, Model;
        internal CoreConfig CoreValues;

        internal static string Resolve(string path, string filename)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Invalid configuration path.", nameof(path));
            return Path.GetFullPath(Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar.ToString()) ||
                path.EndsWith(Path.AltDirectorySeparatorChar.ToString()) ? Path.Combine(path, filename) : path);
        }

        internal static ConfigurationBundle Read(string core, string parameters, string model, JObject parameterValues = null, JObject modelValues = null)
        {
            var result = new ConfigurationBundle { CorePath = core, ParamsPath = parameters, ModelPath = model };
            result.Core = ReadObject(core, JObject.FromObject(new CoreConfig()));
            result.CoreValues = result.Core.ToObject<CoreConfig>();
            if (result.CoreValues == null || result.CoreValues.maxWSS < 1 || result.CoreValues.maxWSS > 3)
                throw new InvalidDataException(core + ": maxWSS must be between 1 and 3.");
            NormalizeAddresses(result.CoreValues);
            // Retain unknown fields in the comparison too. Known fields get the
            // same effective defaults and normalized address representations.
            var normalized = JObject.FromObject(result.CoreValues);
            foreach (var property in normalized.Properties()) result.Core[property.Name] = property.Value.DeepClone();
            foreach (var curve in result.CoreValues.ampCurves ?? Array.Empty<AmpCurveParams>())
            {
                if (curve == null) continue;
                Finite(curve.LowThreshold, core); Finite(curve.LowConst, core); Finite(curve.ExpPower, core);
                Finite(curve.LinearOffset, core); Finite(curve.LinearSlope, core);
            }
            result.Params = parameterValues == null ? ReadObject(parameters, Wss.CalibrationModule.StimParamsConfig.CreateDefaults()) : (JObject)parameterValues.DeepClone();
            if (result.Params["stim"] == null) result.Params["stim"] = new JObject();
            if (!(result.Params["stim"] is JObject stim)) throw new InvalidDataException(parameters + ": stim must be an object.");
            if (stim["ch"] == null) stim["ch"] = new JObject();
            if (!(stim["ch"] is JObject channels)) throw new InvalidDataException(parameters + ": stim.ch must be an object.");
            for (int ch = 1; ch <= result.CoreValues.maxWSS * 3; ch++)
            {
                string name = ch.ToString(CultureInfo.InvariantCulture);
                if (channels[name] == null) channels[name] = new JObject();
                if (!(channels[name] is JObject channel)) throw new InvalidDataException(parameters + ": invalid channel " + name);
                foreach (string leaf in new[] { "minPW", "maxPW", "minPA", "maxPA", "defaultPA", "defaultPW", "IPI" })
                {
                    if (channel[leaf] == null) channel[leaf] = 0;
                    try { Finite(channel[leaf].Value<float>(), parameters + ": stim.ch." + name + "." + leaf); }
                    catch (Exception ex) { throw new InvalidDataException(parameters + ": invalid " + leaf, ex); }
                }
                if (channel["ampMode"] == null) channel["ampMode"] = "PW";
            }
            result.Model = modelValues == null ? ReadObject(model, Wss.ModelModule.ModelConfig.CreateDefaults()) : (JObject)modelValues.DeepClone();
            string mode = (string)result.Model.SelectToken("calib.mode");
            if (mode == null) throw new InvalidDataException(model + ": calib.mode is required.");
            return result;
        }

        private static JObject ReadObject(string path, JObject defaults)
        {
            try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : (JObject)defaults.DeepClone(); }
            catch (Exception ex) { throw new InvalidDataException("Cannot read configuration: " + path, ex); }
        }

        internal static void NormalizeAddresses(CoreConfig config) => CoreConfigController.NormalizeAddresses(config);

        private static void Finite(double value, string path)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidDataException(path + ": expected a finite number.");
        }
    }
}
