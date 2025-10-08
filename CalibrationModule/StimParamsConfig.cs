using Newtonsoft.Json.Linq;

public sealed class StimParamsConfig : DictConfigBase
{
    public StimParamsConfig(string path)
        : base(path, defaults: new JObject
        {
            ["stim"] = new JObject
            {
                ["ch"] = new JObject
                {
                    ["1"] = new JObject { ["maxPW"]=10, ["minPW"]=0, ["amp"]=3.0, ["IPI"]=10 }
                }
            }
        })
    { }
}
