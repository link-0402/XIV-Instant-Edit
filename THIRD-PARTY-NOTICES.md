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
