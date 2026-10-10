# Body physics acceptance

The Body Physics builder (Quick Actions > Body physics, or /ie physics) is covered by
`Tests/ExportContextRegression` (PhysicsScenarios, PhysicsBumpScenarios), which read the game's
body physics files from sqpack. Nothing below has been tried in game yet. Use a test collection,
and keep your own physics mods on so the builder has something to start from.

## Review fixes 2026-10-09 / 2026-10-10 (pending)

Ticked items were verified in game on 2026-10-10 by the XIV Bridge in-game tests
(`XIV-Bridge/tests/instant-edit/physics`, run with `python -I tools/run_tests.py tests/instant-edit/physics`):
real clicks and slider drags in the builder, Penumbra test mods, and checks on the files the character loads.

Committed c3ed475 (review of 06765c8) and 313e1ad.

Preview and saving:
- [x] Let go of a slider with Auto on and close the builder within a second: the preview turns off
  and stays off; opening the builder again doesn't start a preview by itself.
- [x] Let go of a slider and click Save right away: "Saved into ..." stays and the preview doesn't
  come back on.
- [x] Turn my mod off with the preview on: the message reads "Preview off and IE Physics off. Your
  other physics mods apply again." and your character visibly changes.
- [x] Rename the IE Physics mod folder in Penumbra, then save in the builder: it saves into the
  renamed mod; no "IE Physics (2)" appears.

Swapped files:
- [x] Make a test mod with a file swap from chara/human/c0801/skeleton/base/b0001/phy_c0801b0001.phyb
  to the c1401 file. The header reads "the game's file for Female Au Ra" and Loaded now loads the
  c1401 file.
- [x] With a mod that file-swaps your body model, Fit measures the swapped model.

Areas and springs:
- [ ] Add a new area (for example the IVCS butt template) and use Add spring on a chain without
  one: each rests and bounces like the game's own breast physics, not sagging lower.
  Resting is verified (XIV Bridge test 07): the IVCS butt template and a chain given Add spring rest
  within 0.01 cm of their animation pose, like the breasts, while a heavy, loose control sags 7 cm.
  Bouncing still needs checking by eye (it needs the character to move).

Collision with:
- [x] On a preset with links between chains (connectors), select both linked chains and untick a
  shape: under the raw settings the links lose that shape's bit. Tick it again: it comes back.
- [x] Untick a shape for only one of two linked chains: the link keeps its bit and the shape stays
  listed.
- [x] Save a file whose connectors had collision bits, untick an unused shape before them, save
  again and play: the skirt or chain still bumps into the same thighs as before.
