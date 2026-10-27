# Changelog

## 1.0.1

- **The temperature overlay now costs about 8% of one CPU core instead of about 45%.** Before, every frame with temperatures on was decoded, drawn on and re-encoded as a whole JPEG, 30 times a second. Now each clip's frames are re-coded once when they're loaded, losslessly (the same picture, with Huffman tables that can code anything), and the position of every 16x16 block is recorded. Each frame then re-encodes only the blocks under the text, with the text blended in, and copies the rest of the JPEG bit for bit.
- The picture around the temperatures is now the original frame exactly, with no second JPEG pass, so it's slightly sharper than before. The text itself looks the same.
- Memory stays where it was: the large object heap is compacted once after a clip is prepared, and redrawing the text when a reading changes reuses its buffers.
- `ThermalWrong.Cli bench <file>` times the overlay both ways over a whole clip and checks the result against an exact render, and `play` now shows the current share of one core.

## 1.0.0

- First release: your own animations and live CPU and GPU temperatures on the Thermalright Dynamic Vision 360.
