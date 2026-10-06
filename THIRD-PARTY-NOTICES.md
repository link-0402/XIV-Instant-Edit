# Animation implementation references

Instant Edit is licensed under GPL-3.0-or-later; see [LICENSE](LICENSE).

The native animation implementation adapts PAP envelopes, Havok load/save and
interleaved/spline layouts, compression setup, timeline entry layouts, AVFX
dependency fields, emote path conventions, and Penumbra metadata decoding from
[VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor) by 0ceal0t and contributors
(GPL-3.0). The local reference checkout was the
[link-0402 fork](https://github.com/link-0402/Dalamud-VFXEditor), revision
`cebfba38a0b09ef5318f1a86e90c6d96d41a2717`. Relevant source directories are
`Formats/PapFormat`, `Formats/TmbFormat`, `Formats/AvfxFormat`, `Interop/Havok`,
`Interop/Penumbra`, and `Select/Tabs/Emotes`. Penumbra compressed metadata decoding
in that project credits OtterGui by Ottermandias.

Skeleton discovery, lazy metadata caching, and candidate ranking also draw on
the custom `Interop/Havok/SkeletonMatcher.cs` and PAP preview integration in
that local VFXEditor checkout. Instant Edit adds independent source/destination
snapshots, predictive-channel validation, ambiguity handling, and validated
retargeted output; it has no runtime dependency on VFXEditor.

Embedded source discovery also follows the mapper endpoint layout and root
variant traversal in VFXEditor's `Interop/Structs/Animation/SkeletonMapper.cs`,
`SkeletonMapperData.cs`, and `Formats/SklbFormat/Bones/SklbBones.cs`. Local XAT
XML exports are used only for optional asset diagnostics; XAT and the inspected
mod assets are not bundled with Instant Edit or its regression fixtures.

LivePose stack semantics, timeline scope, native CCD and two-joint solver setup,
and idle identification follow [LivePose](https://github.com/Caraxi/LivePose) by
Caraxi and contributors (GPL-3.0), as checked out in LiveAnimationEdit's submodule
at revision `c2b208232cec1039b0006bca1c57dcfddcdc223b`. The adapter binds to the
already-loaded module. LivePose source and binaries are not bundled, modified,
or initialized by Instant Edit. The regression fixture reproduces the small
data shape used by the adapter, rather than loading a game plugin in the test
process.

Ordinary transform semantics were reviewed against the user's BlenderLiveposer
checkout, revision `069469ef78324840156839bf6b5d3601347ff492`: model-space translation
addition, quaternion post-multiplication, and additive scale. Its Python source
is not included. LiveAnimationEdit's static animation-value patcher is not used.

The existing FFXIVClientStructs and Lumina host assemblies provide client
structures and material/model readers. They remain Dalamud-provided dependencies.
Blender add-on notices remain in [Blender-Addon/THIRD_PARTY_NOTICES.md](Blender-Addon/THIRD_PARTY_NOTICES.md).

# Timeline editing

The timeline editor's TMB reader and writer follow VFXEditor's `Formats/TmbFormat`
at the revision named above: entry layouts and sizes, the order of items, extra
data, id lists and strings, and canonical id numbering. Instant Edit also remaps
the F-curve ids that movement entries hold. The GPose preview's playback calls
follow the patterns of [Brio](https://github.com/Etheirys/Brio) and
[Ktisis](https://github.com/ktisis-tools/Ktisis) (both GPL-3.0) through
FFXIVClientStructs members; no code from either is included.

# Motion editor

The Bones window adapts [Brio](https://github.com/Etheirys/Brio) by Etheirys and
contributors (GPL-3.0), revision `73e30cb11be94bb3ca72d062c8305a990f58756b`. Its posing
artwork (`Brio/Resources/Embedded/Images/PoseBody.png`, `PoseArmor.png`, `PoseHand.png`,
`PoseToes.png` and `PoseTail.png`) ships unmodified in `Dalamud-Plugin/Resources/Pose`.
The bone positions on those pictures come from `Resources/Embedded/Data/GraphicalBonePosePositions.json`,
the mirrored layout from `UI/Windows/Specialized/PosingGraphicalWindow.cs`, the rotation
ball from `UI/Controls/Stateless/ImBrio.Gizmo.cs`, the bone groups from
`Resources/Embedded/Data/BoneCategories.json`, and the body part names from
`Resources/Embedded/Language/en.json`. LivePose, which shows the same artwork, is a fork
of Brio.

# Sound import

The SCD sound file layout (header, entry tables, sound, track and audio entries,
and the Vorbis XOR table) follows VFXEditor's `Formats/ScdFormat` at the revision
named above. The MS-ADPCM encoder, WAV reader and resampler are Instant Edit's
own; no external encoder is shipped or run.

Ogg Vorbis decoding uses [NVorbis](https://github.com/NVorbis/NVorbis) 0.10.5 by
Andrew Ward, shipped unmodified as `NVorbis.dll` beside the plugin under the MIT
License:

```text
MIT License

Copyright (c) 2020 Andrew Ward

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
