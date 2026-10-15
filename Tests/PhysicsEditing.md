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
- [ ] Open "Tiramisu without Collision" (its chest spring is -0.001 toward n_root (0, 1, 0)): the
  chest's spring slider reads "Lift", not Firmness. Raise it to about 0.01 and preview: the chest
  sits higher, never lower (the spring stays negative). A file whose chest spring is +0.0005 toward
  the same point reads "Pull down". A new chest area reads "Firmness".
  The mechanics are verified (XIV Bridge, 2026-10-14): toward n_root (0, 1, 0), -0.01 lifted the
  resting tip 6 cm and +0.05 dragged it 11 cm down and in; aimed at rest, 0.002 to 0.2 cut the
  settle time from 41 to 14 frames.

Collision with:
- [x] On a preset with links between chains (connectors), select both linked chains and untick a
  shape: under the raw settings the links lose that shape's bit. Tick it again: it comes back.
- [x] Untick a shape for only one of two linked chains: the link keeps its bit and the shape stays
  listed.
- [x] Save a file whose connectors had collision bits, untick an unused shape before them, save
  again and play: the skirt or chain still bumps into the same thighs as before.

## Plain settings and the overlay (2026-10-14, pending)

Settings:
- [ ] An area shows Firmness (or the spring's own name), Settle, Swing limit, Weight, Follow pose
  and Tip length under Movement; Tip size and Collision with under Collision; and one Advanced
  header holding Max speed, Wind, Friction, Bump damping, Push back, Point shape, Tip place,
  Gravity direction, Wind direction, Steps, the Collision / Fast collision / Ground ticks, and
  Points and Springs inside it. No Fixed length tick anywhere.
- [ ] Drag Settle and hover Undo: the tooltip reads "Chest: Settle 0.100 to 0.200" (your values).

Overlay:
- [ ] With the builder open on Chest, your character shows a line from each breast bone to its tip
  with a ball on the tip, a blue cone from the bone, and a dashed yellow line to the spring's point.
  It follows the character while it walks in GPose.
- [ ] Hover Swing limit: the cone lights and says its angle; drag it and the cone opens or closes.
  Hover Tip size: the balls light. Hover Firmness: the spring's point lights with its name.
- [ ] Only the selected area and the shapes it ticks draw; the file's other shapes don't. Tick a
  thigh under Collision with: its capsule turns green; untick it: it goes. Hover its row while
  unticked: it shows grey with its name.
- [ ] Open Collision shapes: every shape draws green; hover a shape's radius field and it lights.
- [ ] Click the eye: a popup with "Show on your character" and ticks for Area, Tip size, Swing limit,
  Spring, Shapes it collides with and Other shapes. Untick Swing limit: the cones go; hover the
  Swing limit slider: they show lit. Tick Other shapes: the file's shapes draw grey. Untick "Show on
  your character": nothing draws; close and reopen the builder: still off.

## Breast tip place (2026-10-14, pending)

- [ ] Open your IE Physics mod: both chest springs read "Firmness" (0.0005, aimed where the breasts
  rest), not "Pull down".
- [ ] "Fit to my body" lists "Chest, left: tip place" and "Chest, right: tip place", ticked, about
  17.2 to 17.1 cm, tooltip "Moves the tip 1.8 cm onto the front of your breast".
- [ ] With the tip places ticked the tip sizes read about 4.0 cm; untick a place and its size goes
  back to about 6.1 cm.
- [ ] Apply and preview: the overlay's tips sit on your nipples in the standing pose, the cone still
  points through the tip, and the jiggle feels the same as before.
- [ ] With "Both sides" ticked, drag a Tip place field under Advanced: both tips move, the other
  side's to the mirrored place (its third number flips sign on your skeleton). Drag a point's
  Axis in the Points table: the other side's axis mirrors too. With "Both sides" off, only the
  chosen chain moves.

## Breast tip choice (2026-10-14, pending)

- [ ] "Fit to my body" shows "Breast tip: On the nipple / Middle of the breast" above the list, also
  when everything already fits. The choice is remembered for the next fit.
- [ ] Pick "Middle of the breast": the list is measured again (spinner, Apply disabled meanwhile);
  "tip place" now moves the tip into the breast, its tooltip names the new tip length and "about
  N x as much", and the tip size grows to about the breast's own size. Settings you unticked stay
  unticked.
- [ ] Apply and preview with the overlay: the tip balls sit inside the breasts and about fill them.
  Walk and run in GPose: arms crossing in front and at the sides stop at the breast instead of
  sinking into it, and the jiggle is livelier by about the factor the tooltip named.
- [ ] Fit again with "On the nipple": the tips move back to the front and the jiggle returns to how
  it was.

## Named saves (2026-10-14, pending)

The "My physics mod" starting point is gone; "Loaded now" opens whichever saved mod is on.

- [ ] After the update, Penumbra's IE Physics mod is still known: Loaded now reads "IE Physics" in
  green and Save suggests "IE Physics", with "Replaces what you saved in IE Physics."
- [ ] Start from the game's file and Save: the name suggested is "IE Physics (2)" and the popup says
  "Makes a new mod." plus "Your other saved physics mods turn off." Save: a new mod appears in
  Penumbra, the message ends "IE Physics is off.", and IE Physics is off in your collection.
- [ ] Type the name of a mod you didn't make with the builder: Save is refused with "Another mod is
  already called ... Pick another name." Type a name with a slash: Save is disabled.
- [ ] Turn IE Physics back on (and the new one off) in Penumbra, refresh the builder and press Loaded
  now: it opens IE Physics, and Save suggests that name again.
- [ ] Turn mine off: every mod you saved is off and the message names the ones that were on.