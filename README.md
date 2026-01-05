# vinyl-transfer-declick

Application to process digitized vinyls to remove pops, clicks, and other noise.

## Project layout

- `src/VinylTransfer.Core`: Core audio models and processing contracts.
- `src/VinylTransfer.Infrastructure`: WAV I/O and infrastructure services.
- `src/VinylTransfer.UI`: Avalonia UI shell with placeholders for waveform/spectrogram views.

## Next steps

- Wire up UI actions to load WAV files, run processing, and export results.
- Integrate the DSP pipeline with UI controls for auto/manual tuning and progress reporting.
- Replace waveform/spectrogram placeholders with rendered views and analysis overlays.
