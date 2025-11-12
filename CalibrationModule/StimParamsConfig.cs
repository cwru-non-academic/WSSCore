using Newtonsoft.Json.Linq;
using Wss.CoreModule;

/// <summary>
/// JSON-backed stimulation-parameters configuration. Inherits common JSON
/// handling from <see cref="DictConfigBase"/> and seeds default per-channel
/// values under the <c>stim.ch</c> hierarchy.
/// </summary>

namespace Wss.CalibrationModule
{
    public sealed class StimParamsConfig : DictConfigBase
    {
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
