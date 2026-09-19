# Paint.NET — no script needed (probably)

Paint.NET doesn't expose the kind of document/file automation API that
Photoshop (ExtendScript) or GIMP (Script-Fu) do — its plugin SDK is built for
pixel Effects and FileType codecs, not for scripting "flatten and save this
document back to its original path." There's nothing safely equivalent I can
write as an importable script here, and I'd rather say that than hand you
something I can't back up.

The good news: Paint.NET likely already does what you want natively.

## Try this first

Open the `.tga`, edit (add layers, adjustments, whatever), then press
**Ctrl+S** (Save — not Save As). Paint.NET remembers the file it was opened
from, including the format, and on Save it silently flattens the current
layer stack down to what that format supports and overwrites the original
file — no Save As dialog, no path to pick. This is the same "flatten a copy,
keep editing" behavior the Photoshop/GIMP scripts replicate manually, except
Paint.NET already does it as its normal Save behavior for any format that
doesn't support layers.

If your Paint.NET version shows a one-time "the image needs to be flattened"
confirmation on first save, that's expected — it's just a heads-up, not a
path prompt, and doesn't require picking a location each time.

## If that's not enough

If your TGA import/export is coming from an older third-party plugin (e.g. a
"TGA FileType Plus" style plugin on an old Paint.NET version) and Ctrl+S
doesn't overwrite cleanly, or you specifically need to force 32-bit/8bpc
regardless of what Paint.NET wrote, let me know what actually happens when
you try it and I'll build a small external fallback (e.g. a one-click
PowerShell/ImageMagick step that normalizes the saved TGA to 32-bit RGBA in
place) rather than guessing at that now.
