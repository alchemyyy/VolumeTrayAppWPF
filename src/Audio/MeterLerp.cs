namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Step-counter linear-interpolation state machine for the stereo peak meter.
/// Both <see cref="AudioDevice"/> and <see cref="AudioSession"/> compose one of these so the
/// seven-field state plus OnNewSample / OnRenderTick body lives in exactly one place. Mutable
/// struct - the host stores it as a field and the OnSample / OnRenderTick methods mutate it in
/// place. Pass by ref when handing to helpers; pass by value will silently clone the state.
///
/// Thread model: <see cref="WriteRawPeaks"/> is the only call that runs off the UI thread (the
/// sample-timer threadpool worker, after MeterReader.ReadStereoPeaks). Float writes are atomic in
/// .NET and the subsequent OnNewSample is dispatched via Dispatcher.BeginInvoke, providing the
/// release/acquire fence the UI thread needs to observe the new raw values. Everything else
/// (OnNewSample / OnRenderTick / DisplayMin / DisplayMax) is UI-thread only.
/// </summary>
internal struct MeterLerp
{
    // Bg-thread writes these from one IAudioMeterInformation call (min and max over the first
    // two channels). The dispatched UI half copies them into the _target* fields below.
    private float _rawPeakMin, _rawPeakMax;

    // Display fields backing the bound PeakValueMin / PeakValueMax. Mutated only in OnRenderTick.
    private float _displayPeakMin, _displayPeakMax;

    // Lerp origins captured at the start of each sample interval.
    private float _prevPeakMin, _prevPeakMax;

    // Lerp targets - the most recent _raw* values copied across in OnNewSample.
    private float _targetPeakMin, _targetPeakMax;

    // Shared step counter. Both min and max advance in lockstep across _interpolationSteps frames.
    private int _interpolationStep;
    private int _interpolationSteps;

    /// <summary>Current smoothed min(L, R) peak. UI-thread read; never set externally.</summary>
    public readonly float DisplayMin => _displayPeakMin;

    /// <summary>Current smoothed max(L, R) peak. UI-thread read; never set externally.</summary>
    public readonly float DisplayMax => _displayPeakMax;

    /// <summary>
    /// Bg-thread write of the most recently sampled raw peaks. The host has already called
    /// <see cref="MeterReader.ReadStereoPeaks"/> and applied any unified-mode collapse; this
    /// helper just stashes the values for the next OnNewSample dispatch.
    /// </summary>
    public void WriteRawPeaks(float min, float max)
    {
        _rawPeakMin = min;
        _rawPeakMax = max;
    }

    /// <summary>
    /// UI-thread sample-arm. Snapshots the current display values as the new lerp origins,
    /// copies the most recent raw peaks into the target fields, and resets the step counter to
    /// span <paramref name="interpolationSteps"/> render frames (clamped to at least 1).
    /// </summary>
    public void OnNewSample(int interpolationSteps)
    {
        _prevPeakMin = _displayPeakMin;
        _prevPeakMax = _displayPeakMax;
        _targetPeakMin = _rawPeakMin;
        _targetPeakMax = _rawPeakMax;
        _interpolationStep = 0;
        _interpolationSteps = interpolationSteps < 1 ? 1 : interpolationSteps;
    }

    /// <summary>
    /// UI-thread render-tick. Advances the shared step counter and writes the lerped values into
    /// the display fields. <paramref name="minChanged"/> / <paramref name="maxChanged"/> are true
    /// iff the corresponding display field actually moved, so the host can fire PropertyChanged
    /// only when there's a real update to bind.
    /// </summary>
    public void OnRenderTick(out bool minChanged, out bool maxChanged)
    {
        _interpolationStep++;

        float newMin, newMax;
        if (_interpolationStep >= _interpolationSteps)
        {
            // Reached or passed the targets - snap so a long render-only burst (e.g. paused
            // samples) can't drift past the most recent sample.
            newMin = _targetPeakMin;
            newMax = _targetPeakMax;
        }
        else
        {
            float t = (float)_interpolationStep / _interpolationSteps;
            newMin = _prevPeakMin + (_targetPeakMin - _prevPeakMin) * t;
            newMax = _prevPeakMax + (_targetPeakMax - _prevPeakMax) * t;
        }

        minChanged = newMin != _displayPeakMin;
        maxChanged = newMax != _displayPeakMax;
        _displayPeakMin = newMin;
        _displayPeakMax = newMax;
    }

    /// <summary>
    /// Bg-thread reset for the capture-engine-idle path. Forces the raw peaks to 0 so the next
    /// OnNewSample lerps smoothly to silence instead of freezing on whatever the endpoint meter
    /// last reported before Windows idled the engine.
    /// </summary>
    public void PinRawPeaksToSilence()
    {
        _rawPeakMin = 0f;
        _rawPeakMax = 0f;
    }
}
