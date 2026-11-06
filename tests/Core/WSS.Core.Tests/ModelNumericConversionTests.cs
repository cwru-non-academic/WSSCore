using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Wss.CalibrationModule;
using Wss.CoreModule;
using Wss.ModelModule;
using Wss.Testing;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class ModelNumericConversionTests
    {
        private static IEnumerable<TestCaseData> Cases
        {
            get
            {
                yield return Case("integer", new JValue(1), true, 1, 0.5f);
                yield return Case("fraction", new JValue(0.5), true, 0.5f, 0.25f);
                yield return Case("negative", new JValue(-0.5), true, -0.5f, 0);
                yield return Case("zero", new JValue(0), true, 0, 0);
                yield return Case("numeric-string", new JValue("0.25"), true, 0.25f, 0.125f);
                yield return Case("null", JValue.CreateNull(), false, 0, 0.5f);
                yield return Case("boolean", new JValue(true), true, 1, 0.5f);
                yield return Case("object", new JObject(), false, 0, 0.5f);
                yield return Case("array", new JArray(1), false, 0, 0.5f);
                yield return Case("nan", new JValue(float.NaN), true, float.NaN, float.NaN);
                yield return Case("infinity", new JValue(float.PositiveInfinity), true, float.PositiveInfinity, 1);
                yield return Case("negative-infinity", new JValue(float.NegativeInfinity), true, float.NegativeInfinity, 0);
                yield return Case("overflow", new JValue(1e100), true, float.PositiveInfinity, 1);
            }
        }

        private static TestCaseData Case(string id, JToken token, bool accepted, float constant, float output)
            => new TestCaseData(token, accepted, constant, output).SetName("model-conversion." + id);

        [TestCaseSource(nameof(Cases))]
        public void ExistingFloatConversionAndNestedFallbackRemainUnchanged(JToken token, bool accepted,
            float expectedConstant, float expectedOutput)
        {
            string directory = Path.Combine(Path.GetTempPath(), "wss-model-conversion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                WssStimulationSequences.SingleDevice[0].WriteFixture(directory);
                File.WriteAllText(Path.Combine(directory, "modelConfig.json"), new JObject
                {
                    ["calib"] = new JObject { ["mode"] = "P" },
                    ["constants"] = new JObject { ["PModeProportional"] = token, ["PModeOffset"] = 0f },
                    ["PModeProportional"] = 99f
                }.ToString());
                using var core = new WssStimulationCore(new EmulatedWssTransport(),
                    new WssStimulationCoreOptions { ConfigPath = Path.Combine(directory, "stimConfig.json") });
                var real = new StimParamsLayer(core, directory);
                var proxy = DispatchProxy.Create<IStimParamsCore, ModelInitializationFailureTests.RecordingParameters>();
                var recorder = (ModelInitializationFailureTests.RecordingParameters)(object)proxy;
                recorder.Target = real;
                var model = new ModelParamsLayer(proxy, directory);
                bool found = model.GetModelConfigController().TryGetModelParam<float>("constants.PModeProportional", out var value);
                Assert.That(found, Is.EqualTo(accepted));
                AssertFloat(value, expectedConstant);
                model.Initialize();
                model.StimWithMode(2, 0.5f);
                AssertFloat(recorder.LastNormalized, expectedOutput);
                // The recorder captures calculated input before calibration; NaN never reaches a stream encoder.
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void AssertFloat(float observed, float expected)
        {
            if (float.IsNaN(expected)) Assert.That(float.IsNaN(observed), Is.True);
            else Assert.That(observed, Is.EqualTo(expected));
        }
    }
}
