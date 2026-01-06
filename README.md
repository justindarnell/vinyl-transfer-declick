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

## Additional context for the next engineer

- The UI is Avalonia + ReactiveUI. Commands and interactions live in `MainWindowViewModel`.
- Audio I/O uses NAudio via `WavFileService`.
- Processing settings are persisted to `%APPDATA%/VinylTransfer.DeClick/settings.json`.
- DSP changes are centralized in `DspAudioProcessor` and analysis helpers in `AudioAnalysis`.
