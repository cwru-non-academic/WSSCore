using Newtonsoft.Json.Linq;
using Wss.CoreModule;
namespace Wss.CalibrationModule
{
    /// <summary>
    /// JSON-backed stimulation-parameters configuration. Inherits common JSON
    /// handling from <see cref="DictConfigBase"/> and seeds default per-channel
    /// values under the <c>stim.ch</c> hierarchy.
    /// </summary>
    public sealed class StimParamsConfig : DictConfigBase
    {
        /// <summary>
        /// Initializes a stimulation-parameters config backed by the JSON file at <paramref name="path"/>.
        /// Creates the file with defaults when missing.
        /// </summary>
        /// <param name="path">Destination file path.</param>
        public StimParamsConfig(string path)
            : base(path, defaults: new JObject
            {
                ["stim"] = new JObject
                {
                    ["ch"] = new JObject
                    {
                        ["1"] = new JObject
                        {
                            ["ampMode"] = "PW",
                            ["maxPW"] = 0,
                            ["minPW"] = 0,
                            ["maxPA"] = 0.0,
                            ["minPA"] = 0.0,
                            ["defaultPA"] = 1.0,
                            ["defaultPW"] = 50,
                            ["IPI"] = 10
                        }
                    }
                }
            })
        { }
    }
}
