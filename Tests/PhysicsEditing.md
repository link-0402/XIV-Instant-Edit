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
  Partly checked (XIV Bridge tests/instant-edit/physics/07, 2026-10-29): a new Ears area and a chain
  given Add spring rest no lower than the breasts allow (sag 0.00 cm, limit 1.03 cm), while a heavy,
  loose control sags 2.34 cm. Bouncing is by eye (it needs the character to move).
- [x] Open "Tiramisu without Collision" (its chest spring is -0.001 toward n_root (0, 1, 0)): the
  chest's spring slider reads "Lift", not Firmness. Raise it to about 0.01 and preview: the chest
  sits higher, never lower (the spring stays negative). A file whose chest spring is +0.0005 toward
  the same point reads "Pull down". A new chest area reads "Firmness".
  (XIV Bridge tests/instant-edit/physics/21, 2026-10-29, on the game's own file, whose chest springs
  are the same -0.001 toward n_root: both read "Lift"; raised to 0.01 they stay negative, still read
  "Lift" and the tips rest 5.7 cm higher; +0.0005 toward the same point reads "Pull down"; a new
  chest area reads "Firmness". XIV Bridge, 2026-10-14: aimed at rest, 0.002 to 0.2 cut the settle
  time from 41 to 14 frames.)

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
- [x] An area shows Firmness (or the spring's own name), Settle, Swing limit, Weight, Follow pose
  and Tip length under Movement; Tip size and Collision with under Collision; and one Advanced
  header holding Max speed, Wind, Friction, Bump damping, Push back, Point shape, Tip place,
  Gravity direction, Wind direction, Steps, the Collision / Fast collision / Ground ticks, and
  Points and Springs inside it. No Fixed length tick anywhere.
  Partly checked (XIV Bridge tests/instant-edit/physics/11, 2026-10-29): every setting listed is
  where it belongs, Advanced starts closed, and there is no Fixed length tick or slider. Test 11
  still skips the spring slider: the area's first chain has no spring (Add spring shows instead).
  Test 22 sees it on the Chest area of the IE Physics mod: the Firmness slider is drawn and there is
  no Add spring button. (Test 22 again, Release 2026-10-30: the slider is drawn, no Add spring. Ticked:
  between 11 and 22 every setting listed is seen.)
- [x] Drag Settle from 0.100 to 0.200 and hover Undo: the tooltip reads "Chest: Settle 0.200 to 0.100" (your values),
  the change Undo makes; Redo's reads the other way.
  Checked (XIV Bridge tests/instant-edit/physics/12, 2026-10-29), with one question left: after
  dragging Settle from 0.100 to 0.632 the tooltip reads "Chest: Settle 0.632 to 0.100", the way
  Undo will change it (now to before), not before to after as written here. Ruled 2026-10-29: that's right
  (each button's tooltip says what that click changes); the item is reworded to match.

Overlay:
- [x] With the builder open on Chest, your character shows a line from each breast bone to its tip
  with a ball on the tip, a blue cone from the bone, and a dashed yellow line to the spring's point.
  It follows the character while it walks in GPose.
  Partly checked (XIV Bridge tests/instant-edit/physics/13, 2026-10-29): the chain, tip balls, cone
  and dashed spring draw, and over 60 frames every chain point sits within 1 cm of its bone
  (standing). Walking and the colours: by eye.
  (XIV Bridge visual/p13v, Release 2026-10-29: as drawn, the chain lines and tip balls are in the
  accent colour, the swing cone blue, the spring's line dashed yellow; walking in place, the drawn
  chain dots stay on the bones frame after frame.)
- [x] Hover Swing limit: the cone lights and says its angle; drag it and the cone opens or closes.
  Hover Tip size: the balls light. Hover Firmness: the spring's point lights with its name.
  (XIV Bridge tests/instant-edit/physics/14 and 23, 2026-10-29: hovering Swing limit, Tip size and
  Firmness lights the cone, the tip balls and the spring, and nothing else; the labels read 8°,
  1.0 cm and Firmness; dragging Swing limit from 8 to 114 opens the cone's rim from 2.0 to 13.2 cm
  and its label reads 114°.)
- [x] Only the selected area and the shapes it ticks draw; the file's other shapes don't. Tick a
  thigh under Collision with: its capsule turns green; untick it: it goes. Hover its row while
  unticked: it shows grey with its name.
  (XIV Bridge tests/instant-edit/physics/15, 2026-10-15: only ticked shapes draw; Left thigh
  unticked doesn't draw, hovering its row draws it lit with its name, ticked it draws as a used
  shape, unticked again it goes. The colours themselves: by eye.)
  Found 2026-10-29 (XIV Bridge visual/p15v-capsule-colours): a hovered unticked capsule drew green,
  as every hovered shape did. Fixed (ruled grey): PhysicsOverlayModel.ShapeState keeps a hovered
  shape's kind, so an unticked one lights grey; fixture in PhysicsOverlayScenarios. Retest p15v
  after a Release build.
  (XIV Bridge visual/p15v-capsule-colours, Release 2026-10-30 01:48: ticked, the thigh's end circles
  are green; unticked it isn't drawn; unticked with its row hovered it is drawn grey (#595E6E) with
  "Left thigh (asi_l_a)" beside it in the same grey.)
- [x] Open Collision shapes: every shape draws green; hover a shape's radius field and it lights.
  (XIV Bridge tests/instant-edit/physics/15 and 24, 2026-10-29: the shapes view draws all 10 shapes
  the character has bones for, as used shapes, none lit; hovering a shape's Radius field lights that
  shape and no other. The colours themselves: by eye.)
- [x] Click the eye: a popup with "Show on your character" and ticks for Area, Tip size, Swing limit,
  Spring, Shapes it collides with and Other shapes. Untick Swing limit: the cones go; hover the
  Swing limit slider: they show lit. Tick Other shapes: the file's shapes draw grey. Untick "Show on
  your character": nothing draws; close and reopen the builder: still off.
  (XIV Bridge tests/instant-edit/physics/15, 16 and 25, 2026-10-29: the popup has "Show on your
  character" and the six ticks, each click turns its part off and on again and asks to save the
  settings; with Swing limit unticked no cone draws and hovering the slider draws them all lit;
  Other shapes draws the other 10 shapes; with "Show on your character" off nothing draws, also
  after closing and reopening the builder. The colours: by eye.)

## Breast tip place (2026-10-14, pending)

- [ ] Open your IE Physics mod: both chest springs read "Firmness" (0.0005, aimed where the breasts
  rest), not "Pull down".
  Not true on the saved mod (XIV Bridge tests/instant-edit/physics/22, 2026-10-29): both chest springs
  read "Pull down" and point about 58° from where the breasts rest. The naming code is consistent
  (Firmness under 15°), so the saved springs were likely aimed at an older tip place (a fit re-aims
  springs only when it moves the tip). Both are 0.0005 toward j_sebo_b, as the item says.
  Ruled 2026-10-29: add "Aim at rest" rather than re-aim saved springs on their own. Added: the spring row of a
  spring more than 15° off shows "Aim at rest" (tooltip "Turns the spring to pull the tip to where it rests, keeping
  its strength. It points 58° off now."); a click turns it toward the rest place, keeps its strength, both sides with
  "Both sides" ticked, one undo step (fixture: 58° off becomes Firmness, under 0.05° off). Pending in game: on a
  XIVBridgeTest copy of IE Physics, the button shows on "Pull down", the row reads "Firmness" after it, Undo puts it
  back; the chest settles where it rests (by eye).
  Partly checked (XIV Bridge physics/35, Release 2026-10-30 01:48, a real click on the copy's chest with Both sides):
  "Aim at rest" shows beside the "Pull down" slider; the click turns both springs from 57.6 and 57.8 deg off to 0.0,
  both read "Firmness", strength kept at 0.0005, the button goes; one Undo puts both back at "Pull down". Left: the
  chest settling where it rests, by eye. (physics/22 now reports the saved springs instead of failing on them.)
- [x] "Fit to my body" lists "Chest, left: tip place" and "Chest, right: tip place", ticked, about
  17.2 to 17.1 cm, tooltip "Moves the tip 1.8 cm onto the front of your breast".
  (XIV Bridge tests/instant-edit/physics/17, 2026-10-15: both listed and ticked, 17.2 to 15.4 cm,
  tooltip "Moves the tip 2.0 cm onto the front of your breast, where the game's tips sit.")
- [ ] With the tip places ticked the tip sizes read about 4.0 cm; untick a place and its size goes
  back to about 6.1 cm.
  Partly checked (XIV Bridge tests/instant-edit/physics/17 and 26, 2026-10-29): the tip size changes
  with the place and stays within 1-15 cm: 4.0 cm ticked and 7.6 cm unticked in 17; 7.2 and 4.3 cm in
  26. Those are not the numbers written here: about 4.0 and 6.1 cm are for your fitted file. On the
  game's own file the chest chains collide with nothing, so no tip-size row follows the place; 26
  checks only that the size changes and stays within 1-15 cm.
- [ ] Apply and preview: the overlay's tips sit on your nipples in the standing pose, the cone still
  points through the tip, and the jiggle feels the same as before.
  Partly checked (XIV Bridge visual/p29s, Release 2026-10-29): the walking chest swing after the
  nipple fit is what the tooltip names. Judged by Claude from a front crop with the overlay on: each
  tip sits at the front centre of its breast with the cone around it; a front view can't show depth,
  so "on the nipple" wants a side view.
- [x] With "Both sides" ticked, drag a Tip place field under Advanced: both tips move, the other
  side's to the mirrored place (its third number flips sign on your skeleton). Drag a point's
  Axis in the Points table: the other side's axis mirrors too. With "Both sides" off, only the
  chosen chain moves.
  Partly checked (XIV Bridge tests/instant-edit/physics/18, 2026-10-29): a real drag of Tip place
  with Both sides on moved j_mune_l and j_mune_r to mirrored places (third number flipped, within
  1 mm); with it off only j_mune_l moved. The Points table's Axis isn't checked: test 27 fails
  tonight because it can't find the Axis field (it isn't drawn and in view), a test problem, not a
  product bug.
  (XIV Bridge physics/27, Release 2026-10-30: a real drag of the first point's Axis with Both sides on
  moved it from (0.010, 0.097, 0.023) to (0.160, 0.097, 0.023) and the twin's to (0.160, 0.097, -0.022),
  the mirror; with Both sides off only the chosen chain's axis moved. 27 couldn't find the field because
  the bridge hashed the table row's PushId(n) as a 4-byte int; Dalamud pushes ints as pointers, 8 bytes:
  the bridge now takes "#ptr:n" for those.)

## Breast tip choice (2026-10-14, pending)

- [x] "Fit to my body" shows "Breast tip: On the nipple / Middle of the breast" above the list, also
  when everything already fits. The choice is remembered for the next fit.
  (XIV Bridge tests/instant-edit/physics/19 and 28, 2026-10-29: both choices are offered, also once
  no ticked setting changes any more; picking Middle asks to save it and the next fit keeps it.)
- [x] Pick "Middle of the breast": the list is measured again (spinner, Apply disabled meanwhile);
  "tip place" now moves the tip into the breast, its tooltip names the new tip length and "about
  N x as much", and the tip size grows to about the breast's own size. Settings you unticked stay
  unticked.
  Partly checked (XIV Bridge tests/instant-edit/physics/19, 2026-10-29): it measures again; the
  tooltip reads "Moves the tip 2.8 cm into the middle of your breast ... Tip length 10.9 to 8.2 cm
  ... about 1.3x as much"; the tip size grows from 4.0 to 7.2 cm; an unticked Left thigh stays
  unticked. The measuring was over before the test could see Apply disabled.
  (XIV Bridge physics/19, Release 2026-10-30 01:48, the measuring held open by a framework hook: Apply's text is drawn at
  0.60 alpha while it measures and 1.00 before and after, as the disabled tip choice's is (1.00 to 0.60); the tooltip reads
  "Moves the tip 4.9 cm into the middle of your breast ... Tip length 10.9 to 6.1 cm ... about 1.8x as much", the tip size
  grows from 4.0 to 9.1 cm, an unticked Left thigh stays unticked. The old check found Apply with Ui.Locate, which also
  finds disabled items, so it could never have failed.)
- [ ] Apply and preview with the overlay: the tip balls sit inside the breasts and about fill them.
  Walk and run in GPose: arms crossing in front and at the sides stop at the breast instead of
  sinking into it, and the jiggle is livelier by about the factor the tooltip named.
  Partly checked (XIV Bridge visual/p29s, Release 2026-10-29): walking in place, the jiggle changes by
  the tooltip's factor (within 25%); judged by Claude from a front crop, the tip balls are about the
  breasts' size (a little larger on the right). Left: arms crossing stop at the breast.
- [x] Fit again with "On the nipple": the tips move back to the front and the jiggle returns to how
  it was.
  Partly checked (XIV Bridge tests/instant-edit/physics/29, 2026-10-29): after the Middle fit, the
  nipple fit puts both tips back on the front (within 2 mm) at the same size, with the swing cone
  and the spring's point and strength as they were. The jiggle's feel: by eye.
  (XIV Bridge visual/p29s, Release 2026-10-29: walking in place, the jiggle after the second nipple
  fit is back within 15% of the first.)

## Named saves (2026-10-14, pending)

The "My physics mod" starting point is gone; "Loaded now" opens whichever saved mod is on.

- [ ] After the update, Penumbra's IE Physics mod is still known: Loaded now reads "IE Physics" in
  green and Save suggests "IE Physics", with "Replaces what you saved in IE Physics."
  Partly checked (XIV Bridge tests/instant-edit/physics/20, 2026-10-29, on a copy named
  "XIVBridgeTest IE Physics"): Loaded now opens it and Save suggests its name and says it replaces
  what you saved there. The move from the old single mod after the update and the green colour: by
  eye.
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

## Skirt fit to Customize+ (2026-10-23, pending)

Physics runs before Customize+, so the fit now places skirt shapes where Customize+ draws the body.
Use a profile that moves the thighs (yours: j_asi_a 3 cm, j_asi_b about 1.4 cm) and a skirt mod
that uses the game's j_sk bones.

- [x] "Fit to my body": "Left thigh (...)" and "Right thigh (...)" read "..., moved N cm" in Fits,
  and the tooltip ends "Moves it N cm to where Customize+ puts your body".
  (XIV Bridge tests/instant-edit/physics/30, 2026-10-29: both thighs read "moved 3.0 cm"; the
  tooltip reads "... Moves it 3.0 cm to where Customize+ puts your body, since physics runs before
  it.")
- [ ] "Left/Right butt cheek, for the skirt" are listed unticked, Now "none". Tick both and Apply.
  Not checked (XIV Bridge tests/instant-edit/physics/30, 2026-10-29): the butt cheeks aren't offered
  on this skeleton, which lacks iv_shiri and ya_shiri_phys bones, so the skirt gets no butt
  collision.
  Still not checked (XIV Bridge physics/30 and 34 with --arg yas=true, Release 2026-10-30 01:48): with Yet Another Skeleton's
  bones the fit notes "none of your skin moves with ya_shiri_phys_l" (and _r): the body model isn't weighted to butt bones, so
  no ball is offered. Needs a body weighted to YAS or IVCS butt bones. (Same for the two butt items below.)
- [x] Under the list, "Waistband: set in Customize+" names the skirt bones with X/Y/Z moves.
  Enter them in your Customize+ profile's Position and check the waistband in game.
  Partly checked (XIV Bridge tests/instant-edit/physics/30, 2026-10-29): it names j_sk_f_a, j_sk_s_a
  and j_sk_b_a, left and right, with X/Y/Z moves of 0.9 to 1.2 cm out. Entering them in Customize+
  and looking at the waistband: by hand.
  (XIV Bridge physics/33, Release 2026-10-29: the moves entered in a temporary Customize+ profile
  (the player's own plus them) move each named bone about the fit's distance, away from the hips.
  How the waistband looks: by eye.)
- [ ] Preview, standing in GPose: the skirt hangs off the thighs and butt instead of through them.
  Walk and run: the back of the skirt slides over the butt. Wrong would look like the skirt held
  further off one side of a thigh than before while sinking into the other side: that would mean
  physics sees Customize+ after all (Customize+ applies in a render hook, so it shouldn't).
  On your IE Physics file both thigh capsules sit on j_asi_a, which your profile moves mostly
  along the leg, so expect them to move about 3 cm down the leg and only about 0.5 cm sideways.
  Partly checked (XIV Bridge physics/34, Release 2026-10-29): previewed standing, walking and running
  in place, the fitted skirt's j_sk bones keep out of the thighs as drawn at least as well as before
  the fit, and aren't held further off one side while sinking into the other. Left: the back sliding
  over the butt (no butt bones on this skeleton), and the cloth by eye.
- [ ] Fit again: the butt cheeks are no longer unticked extras and nothing else changes.
  Partly checked (XIV Bridge tests/instant-edit/physics/30, 2026-10-29): a second fit lists 0
  changes. The butt cheeks part wasn't tried, as they aren't offered here.
- [ ] Known: the overlay draws shapes on the pose after Customize+, so moved skirt shapes show
  off the leg by the Customize+ move there. Judge by the skirt, not the overlay.

## Adding areas back

- [x] Untick the skirt (or delete it), save, and reopen with Loaded now: "+ Skirt" is listed under
  Add an area. Click it: the skirt comes back with its thigh shapes, and the preview swings it again.
  XIV Bridge tests/instant-edit/physics/31 errors tonight (2026-10-29): it can't find the tick or name
  of area 0 in the area list. A test problem, not a product bug.
  (XIV Bridge physics/31, Release 2026-10-30, on the test copy: a real click on the skirt's tick leaves it
  out, the save goes into the copy, Loaded now opens it with no skirt, "+ Skirt" is offered and drawn, a
  click brings the game's skirt back with its chains and the shapes it collides with (asi_r_c, asi_l_c,
  asi_r_a, asi_l_a, the thighs), and the previewed file moves the skirt bones again; the swing itself
  by eye. Same cause as 27: the area list's PushId(i) needs "#ptr:i".)
- [x] Same for the tail on a Miqo'te or Au Ra, and for the chest or butt: the chest and other jiggle
  areas are built on your skeleton, the skirt, tail, earrings and hair are the game's own.
  Partly checked (XIV Bridge tests/instant-edit/physics/32, 2026-10-29): after deleting the tail,
  "+ Tail" is offered from the game's file and brings back the same bones (n_sippo_b), chain and tip
  places; after deleting the chest, "+ Chest" is offered as a template and builds j_mune_l and
  j_mune_r on this skeleton. The butt, earrings and hair weren't tried (the templates offered were
  chest and ears).
  (XIV Bridge physics/36, Release 2026-10-30 01:48, with a test mod holding only Yet Another Skeleton's c0801 body skeleton:
  "+ Butt (YAS)" is offered as a template, not from the game, and builds ya_shiri_phys_l/r with the template's cones and
  springs measured on that skeleton; Earrings, Hair and Front hair have no template, and each, deleted, comes back from the
  game's c0801 file with its own bones (n_ear_b_l/r, j_kami_a, j_kami_f_l, j_kami_f_r), chains and tip places.)

## Fit: parts and any slot (2026-11-01, pending)

Balls on a breast's own bone fit the breast you see, fitted sizes keep 1 cm clear where physics runs, models on any
slot count for the areas they move with, and genital tips are placed. Use a test copy of Ashley Jiggles Customized
(its chest is VFXEditor's: the bone, a tip without a bone, then iv_c_mune_l).

- [x] The game aims a chain's bone at its first point without a bone, so that point is the tip.
  (XIV Bridge physics/37, 2026-11-01, Lucina standing: Ashley's spring on that point, pulled 30 cm to the left, turned
  j_mune_l 29.6°; the same spring on iv_c_mune_l turned it 2.2°.)
- [ ] On a character whose Customize+ grows the breasts: Fit to my body (Middle of the breast), then Preview. The breasts
  rest where they did before the fit: j_mune_l and j_mune_r within 2° standing. (XIV Bridge physics/38.)
- [ ] The breast balls read "Moves it N cm to the middle of your left breast and sizes it to the breast." and stay under
  10 cm. A row the fit holds back ends "Kept 1 cm off ..., which it would push all the time." (physics/38 checks the
  sizes and that no body area's point comes within 1 cm of a shape at rest; the sentences by eye.)
- [ ] The chest overlay shows the tip, the cone and the spring. (physics/38.)
- [ ] On a character whose member is a ring model: the fit lists "Penis: tip place" and the testicles' tip place and size,
  and its notes say "Your right ring's model moves with iv_ochinko_a, so it was measured for the penis." (physics/39.)
- [ ] Applied, the penis tip sits at the end of the member and the testicles' in their middle; the sizes look right (by
  eye). (physics/39 checks the penis tip.)
- [ ] Walk and run: the arms stop at the breasts and the breasts don't knock each other apart (by eye).
- [ ] Both sides: Tip place on Ashley's chest moves both tips, the other side mirrored (by hand).
