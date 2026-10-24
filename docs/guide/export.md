# Export the audiobook

Assemble the finished audio into one audiobook file with chapters and cover art. The result is a single `.m4b` with one chapter marker per volume, part and chapter. It carries the book title, the author and the project's cover.

## How

1. Choose **Assemble** to open the Export page. Under **Readiness**, check how many items have audio and that ffmpeg is found. Assembly needs only ffmpeg, not the TTS or LLM services. Set its path in Settings, **Audio processing**, and use **Test ffmpeg** to check it.
2. Choose **Assemble**. Progress runs through **Gather**, **Silence**, **Probe & concat**, **Encode** and **Finalize**. **Cancel** stops it. Only one project assembles at a time.
3. If some items still lack audio, ReadToMe offers a partial build. It leaves those items out and saves the file with `_partial_` and the date in its name. Use it to listen to a draft, then finish the audio for the real build.
4. Download the file from **Outputs**. Builds are kept in the project's `output` folder. A full build is named after the book title, and a new full build replaces the previous one.

After any change that needs new audio, generate it on the Audio step, then assemble again. Changing pause durations or the cover only needs a new assembly.

## Done when

A full build exists. The Export row shows **Done** with the date of the last build.
