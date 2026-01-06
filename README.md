# vinyl-transfer-declick

Application to process digitized vinyls to remove pops, clicks, and other noise.

## Project layout

- `src/VinylTransfer.Core`: Core audio models and processing contracts.
- `src/VinylTransfer.Infrastructure`: WAV I/O and infrastructure services.
- `src/VinylTransfer.UI`: Avalonia UI shell with waveform preview, processing controls, and playback.

## Recent project history

- Implemented a full UI workflow (import → analyze → preview → export) with persistent tuning controls.
- Added waveform rendering, preview playback, and file dialog wiring in the Avalonia UI.
- Expanded DSP processing with median-based repair, spectral noise reduction, and multi-band transient detection.
- Introduced automated analysis and recommendations (segment-based thresholds and SNR estimates).
- Persisted user settings between runs via a JSON settings store.

## How the processing works today

- Manual controls adjust click/pop thresholds, intensities, and noise reduction.
- Optional DSP passes include:
  - Median-based repair for spikes.
  - Spectral noise reduction for steady hiss.
  - Multi-band transient detection to catch subtle ticks.
- Automated recommendations scan the audio to suggest initial settings and enable the new passes by default.

## Potential next steps

- Add per-band transient density analysis to drive adaptive thresholds over time.
- Improve spectral noise reduction with psychoacoustic masking and better noise profiling.
- Add A/B playback scrubbing and timeline markers for clicks/pops.
- Extend waveform view to show detected transient overlays and noise profiles.
- Add batch processing and presets for common vinyl cleanup scenarios.
- Add automated tests for DSP correctness and regression coverage.

## Targeted upgrade plan (quality priorities)

### Phase 1 — Stabilize noise modeling and reduce artifacts

- Unify noise-floor estimation between analysis and processing using segment-based percentiles to avoid inconsistent auto-mode behavior.
- Replace time-domain gating with a gentle spectral floor in the FFT path to prevent pumping in quiet passages.
- Add temporal smoothing to spectral attenuation to reduce musical-noise artifacts.
- Make transient detection window size adaptive to sample rate to preserve timing consistency.

### Phase 2 — Improve click/pop fidelity

- Introduce multi-sample impulse validation (short-window energy + HF emphasis) before declaring a click/pop.
- Add band-limited interpolation repair (or AR/wavelet-based inpainting) for cleaner transient reconstruction.
- Add a dedicated decrackle pass tuned for dense micro-impulses before click/pop repair.

### Phase 3 — Listening workflow and validation

- Add objective regression metrics (SNR improvement, residual click count, delta RMS) with curated test vectors.
- Expose detected events and noise profile overlays in the waveform view for targeted review.
- Add presets for common vinyl profiles and A/B auditioning for rapid tuning.

## Additional context for the next engineer

- The UI is Avalonia + ReactiveUI. Commands and interactions live in `MainWindowViewModel`.
- Audio I/O uses NAudio via `WavFileService`.
- Processing settings are persisted to `%APPDATA%/VinylTransfer.DeClick/settings.json`.
- DSP changes are centralized in `DspAudioProcessor` and analysis helpers in `AudioAnalysis`.
