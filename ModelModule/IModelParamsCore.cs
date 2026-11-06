using Wss.CalibrationModule;
namespace Wss.ModelModule
{
    /// <summary>
    /// Model-driven stimulation surface layered over params + core.
    /// Extends the params API and adds controller-mode stimulation (P/PD).
    /// </summary>
    public interface IModelParamsCore : IStimParamsCore
    {
        /// <summary>
        /// Validates the current controller mode from model config (e.g., <c>P</c> or <c>PD</c>).
        /// </summary>
        /// <returns><c>true</c> if the mode is supported; otherwise <c>false</c>.</returns>
        /// <example>
        /// <code>
        /// if (!model.IsModeValid()) { /* fallback or warn */ }
        /// </code>
        /// </example>
        bool IsModeValid();

        /// <summary>
        /// Computes a pulse width from <paramref name="magnitude"/> using the active controller mode
        /// and stimulates the resolved channel. Updates cached per-channel PW and amp internally.
        /// </summary>
        /// <param name="ch">1-based channel index.</param>
        /// <param name="magnitude">Normalized input typically in [0..1]. Values are clamped.</param>
        /// <example>
        /// <code>
        /// model.StimWithMode("index", 0.42f);     // P/PD mapping → PW → StimulateAnalog
        /// model.StimWithMode("ch3",   0.75f);
        /// </code>
        /// </example>
        void StimWithMode(int ch, float magnitude);

        /// <summary>
        /// Returns the model-layer configuration controller used to read and write model constants
        /// (e.g., proportional/derivative gains, offsets, and mode selection).
        /// </summary>
        /// <returns>The active <see cref="ModelConfigController"/> instance.</returns>
        /// <remarks>
        /// Use dotted keys such as <c>constants.PModeProportional</c> to query or persist model settings.
        /// Modeled stimulation reads only nested constants; top-level overrides are ignored.
        /// </remarks>
        /// <example>
        /// <code>
        /// var mc = model.GetModelConfigController();
        /// float kp = mc.GetModelParam&lt;float&gt;("constants.PModeProportional");
        /// mc.SetModelParam("constants.PDModeDerivative", 0.15f);
        /// </code>
        /// </example>
        ModelConfigController GetModelConfigController();
    }
}
