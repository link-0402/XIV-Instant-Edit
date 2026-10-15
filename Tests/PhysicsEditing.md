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

Checked in game on 2026-10-15 by the XIV Bridge tests `tests/instant-edit/physics/11` to `20`, on a
copy of the IE Physics mod in the test collection (build 6297dbaa, main 7b74c4f). Colours, the
jiggle's feel and walking in GPose are left for your eye.

Settings:
- [ ] An area shows Firmness (or the spring's own name), Settle, Swing limit, Weight, Follow pose
  and Tip length under Movement; Tip size and Collision with under Collision; and one Advanced
  header holding Max speed, Wind, Friction, Bump damping, Push back, Point shape, Tip place,
  Gravity direction, Wind direction, Steps, the Collision / Fast collision / Ground ticks, and
  Points and Springs inside it. No Fixed length tick anywhere.
  Partly checked (XIV Bridge tests/instant-edit/physics/11, 2026-10-15): every setting listed is
  where it belongs, Advanced starts closed, and there is no Fixed length tick or slider. The spring
  slider wasn't seen: the area's first chain had no spring (Add spring showed instead).
- [ ] Drag Settle and hover Undo: the tooltip reads "Chest: Settle 0.100 to 0.200" (your values).
  Checked (XIV Bridge tests/instant-edit/physics/12, 2026-10-15), with one question left: after
  dragging Settle from 0.100 to 0.632 the tooltip reads "Chest: Settle 0.632 to 0.100", the way
  Undo will change it (now to before), not before to after as written here.

Overlay:
- [ ] With the builder open on Chest, your character shows a line from each breast bone to its tip
  with a ball on the tip, a blue cone from the bone, and a dashed yellow line to the spring's point.
  It follows the character while it walks in GPose.
  Partly checked (XIV Bridge tests/instant-edit/physics/13, 2026-10-15): the chain, tip balls, cone
  and dashed spring draw, and over 60 frames every chain point sits within 1 cm of its bone
  (standing). Walking and the colours: by eye.
- [ ] Hover Swing limit: the cone lights and says its angle; drag it and the cone opens or closes.
  Hover Tip size: the balls light. Hover Firmness: the spring's point lights with its name.
  Partly checked (XIV Bridge tests/instant-edit/physics/14, 2026-10-15): hovering Swing limit, Tip
  size and Firmness lights the cone, the tip balls and the spring, and nothing else. The angle and
  name labels and the cone following a drag: by eye.
- [x] Only the selected area and the shapes it ticks draw; the file's other shapes don't. Tick a
  thigh under Collision with: its capsule turns green; untick it: it goes. Hover its row while
  unticked: it shows grey with its name.
  (XIV Bridge tests/instant-edit/physics/15, 2026-10-15: only ticked shapes draw; Left thigh
  unticked doesn't draw, hovering its row draws it lit with its name, ticked it draws as a used
  shape, unticked again it goes. The colours themselves: by eye.)
- [ ] Open Collision shapes: every shape draws green; hover a shape's radius field and it lights.
  Partly checked (XIV Bridge tests/instant-edit/physics/15, 2026-10-15): the shapes view draws all
  10 shapes the character has bones for. Hovering a radius field wasn't tried.
- [ ] Click the eye: a popup with "Show on your character" and ticks for Area, Tip size, Swing limit,
  Spring, Shapes it collides with and Other shapes. Untick Swing limit: the cones go; hover the
  Swing limit slider: they show lit. Tick Other shapes: the file's shapes draw grey. Untick "Show on
  your character": nothing draws; close and reopen the builder: still off.
  Partly checked (XIV Bridge tests/instant-edit/physics/16 and 15, 2026-10-15): the popup has
  "Show on your character" and the six ticks, each click turns its part off and on again and asks
  to save the settings; Other shapes draws the other 10 shapes; with "Show on your character" off
  nothing draws, also after closing and reopening the builder. Hovering Swing limit with its cones
  off wasn't tried.

## Breast tip place (2026-10-14, pending)

- [ ] Open your IE Physics mod: both chest springs read "Firmness" (0.0005, aimed where the breasts
  rest), not "Pull down".
- [x] "Fit to my body" lists "Chest, left: tip place" and "Chest, right: tip place", ticked, about
  17.2 to 17.1 cm, tooltip "Moves the tip 1.8 cm onto the front of your breast".
  (XIV Bridge tests/instant-edit/physics/17, 2026-10-15: both listed and ticked, 17.2 to 15.4 cm,
  tooltip "Moves the tip 2.0 cm onto the front of your breast, where the game's tips sit.")
- [ ] With the tip places ticked the tip sizes read about 4.0 cm; untick a place and its size goes
  back to about 6.1 cm.
  Not shown (XIV Bridge tests/instant-edit/physics/17, 2026-10-15): the test copy of the mod was
  fitted before, so both tip sizes read 4.0 cm ticked and unticked.
- [ ] Apply and preview: the overlay's tips sit on your nipples in the standing pose, the cone still
  points through the tip, and the jiggle feels the same as before.
- [ ] With "Both sides" ticked, drag a Tip place field under Advanced: both tips move, the other
  side's to the mirrored place (its third number flips sign on your skeleton). Drag a point's
  Axis in the Points table: the other side's axis mirrors too. With "Both sides" off, only the
  chosen chain moves.
  Partly checked (XIV Bridge tests/instant-edit/physics/18, 2026-10-15): a real drag of Tip place
  with Both sides on moved j_mune_l and j_mune_r to mirrored places (third number flipped, within
  1 mm); with it off only j_mune_l moved. The Points table's Axis wasn't tried.

## Breast tip choice (2026-10-14, pending)

- [ ] "Fit to my body" shows "Breast tip: On the nipple / Middle of the breast" above the list, also
  when everything already fits. The choice is remembered for the next fit.
  Partly checked (XIV Bridge tests/instant-edit/physics/19, 2026-10-15): both choices are offered,
  picking one asks to save it, and the next fit opens with Middle. The "everything already fits"
  case wasn't set up.
- [ ] Pick "Middle of the breast": the list is measured again (spinner, Apply disabled meanwhile);
  "tip place" now moves the tip into the breast, its tooltip names the new tip length and "about
  N x as much", and the tip size grows to about the breast's own size. Settings you unticked stay
  unticked.
  Partly checked (XIV Bridge tests/instant-edit/physics/19, 2026-10-15): it measures again; the
  tooltip reads "Moves the tip 9.1 cm into the middle of your breast ... Tip length 17.2 to 8.2 cm
  ... about 2.1x as much"; the tip size grows from 4.0 to 7.2 cm; an unticked Left thigh stays
  unticked. The measuring was over before the test could see Apply disabled.
- [ ] Apply and preview with the overlay: the tip balls sit inside the breasts and about fill them.
  Walk and run in GPose: arms crossing in front and at the sides stop at the breast instead of
  sinking into it, and the jiggle is livelier by about the factor the tooltip named.
- [ ] Fit again with "On the nipple": the tips move back to the front and the jiggle returns to how
  it was.

## Named saves (2026-10-14, pending)

The "My physics mod" starting point is gone; "Loaded now" opens whichever saved mod is on.

- [ ] After the update, Penumbra's IE Physics mod is still known: Loaded now reads "IE Physics" in
  green and Save suggests "IE Physics", with "Replaces what you saved in IE Physics."
  Partly checked (XIV Bridge tests/instant-edit/physics/20, 2026-10-15, on a copy named
  "XIVBridgeTest IE Physics"): Loaded now opens it and Save suggests its name as one of yours. The
  move from the old single mod after the update and the green colour: by eye.
- [x] Start from the game's file and Save: the name suggested is "IE Physics (2)" and the popup says
  "Makes a new mod." plus "Your other saved physics mods turn off." Save: a new mod appears in
  Penumbra, the message ends "IE Physics is off.", and IE Physics is off in your collection.
  (XIV Bridge tests/instant-edit/physics/20, 2026-10-15: the suggested name was a free one, "IE
  Physics", since the saved mod was the test copy; the new mod appeared and is on in the test
  collection, the message ended "XIVBridgeTest IE Physics is off." and that mod is off.)
- [x] Type the name of a mod you didn't make with the builder: Save is refused with "Another mod is
  already called ... Pick another name." Type a name with a slash: Save is disabled.
  (XIV Bridge tests/instant-edit/physics/20, 2026-10-15: refused with "Another mod is already called
  XIVBridgeTest not the builder's 40cc. Pick another name."; with "XIVBridgeTest a/b" clicking Save
  does nothing and no mod appears.)
- [x] Turn IE Physics back on (and the new one off) in Penumbra, refresh the builder and press Loaded
  now: it opens IE Physics, and Save suggests that name again.
  (XIV Bridge tests/instant-edit/physics/20, 2026-10-15, with the test copy in its place.)
- [x] Turn mine off: every mod you saved is off and the message names the ones that were on.
  (XIV Bridge tests/instant-edit/physics/20, 2026-10-15: both saved mods off; "XIVBridgeTest IE
  Physics is off. Your other physics mods apply again." names only the one that was on.)