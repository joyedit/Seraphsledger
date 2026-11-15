#!/usr/bin/env python3
"""Builds the paste-ready ModDB description for The Seraph's Ledger.

mods.vintagestory.at strips <style> blocks and ignores class= in a mod description, but it
keeps inline style="..." attributes. So a page that keeps its design in a stylesheet arrives
on the ModDB as a wall of unstyled text. This script emits the content with every rule
inlined, and wraps the same fragment in a standalone document for checking it in a browser.

    python3 docs/build-moddb-description.py

Writes:
    docs/seraphsledger-moddb.html   the fragment to paste into the ModDB description field
    docs/seraphsledger.html         the same fragment in a full page, for local preview

    docs/moddb-description.html is the old stylesheet-era page this one was converted from.

SIZE IS THE BINDING CONSTRAINT HERE - read this before adding anything.

The ModDB refuses a description that is too big: it rejected this page at 87KB, and again at
what it measured as 85KB from a 53KB file. The second number is the important one. The site's
editor re-serialises whatever you paste before it measures and stores it, and that rewrite
inflates inline CSS by about 60%:

    border-bottom:1px solid #DAD3C4        31 bytes, as written here
    border-bottom-width: 1px; border-bottom-style: solid; border-bottom-color: rgb(218, 211, 196);
                                           94 bytes, as the editor stores it

Every shorthand expands, every #hex becomes rgb(r, g, b), and every declaration gains its
spaces back. So the budget is not what this file weighs, it is what this file weighs times
roughly 1.6. The largest live description found on the site is Primitive Survival at 54KB
stored, so this page aims at 32KB here, which lands near 50KB there.

What that buys, and what it costs:
  - No tables. A table is the most expensive thing on a page - every cell carries its own
    padding and border, and both of those treble in the rewrite. The feature list, the
    container recipes and the commands are plain <ul> instead. The only table left is the
    four-figure strip under the masthead.
  - Styling sits on the parent wherever it can: colour, line-height, font-size and text-align
    all inherit, so they belong on one <ul>, not on sixty <li> elements.
  - <code> is monospace in every browser without being told, so it is never given a font
    stack, and it carries one declaration rather than four.
  - The writing is condensed: one paragraph per feature, not a paragraph and six bullets. The
    full detail lives in the repo's CHANGELOG, which is where anyone who wants that much is
    going anyway.
  - If it has to shrink again: drop a whole aside before thinning the prose, and never bring
    a table back.

Other house rules:
  - Content lives here and nowhere else; both files are generated, so they cannot drift.
  - Keep the switch count in step with the Feature[] array in src/GuiDialogFeatureConfig.cs.
  - No backtick characters anywhere in the text, apostrophes are written &#39; as you go
    (never regex-swept - a sweep eats the f' that opens every f-string), and the AI
    disclosure stays last.
  - No author byline and no source link: Jay links the source elsewhere on each mod page.

Derived from ~/Dev/FeedorFallow/docs/build-moddb-description.py - the fleet template.
"""
import os
import re

PAPER   = "#F1EDE4"
SURFACE = "#F9F6EF"
INK     = "#1C1A17"
INK2    = "#4A463D"
INK3    = "#79736A"
RULE    = "#DAD3C4"
RULES   = "#BDB4A2"
QUILL   = "#5E3A6E"

# Short on purpose: a font stack is stamped into every element that names it.
BODY = "-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif"
DISP = "Georgia,'Times New Roman',serif"
MONO = "ui-monospace,Menlo,Consolas,monospace"

BUDGET = 32 * 1024


def p(text, mt="0", mb="14px", color=None, size=None):
    extra = f" font-size:{size};" if size else ""
    extra += f" color:{color};" if color else ""
    return f'<p style="margin:{mt} 0 {mb};{extra}">{text}</p>'


def h2(text):
    return (f'<h2 style="font-family:{DISP}; font-size:1.6em; line-height:1.15; margin:36px 0 14px;'
            f' padding-top:24px; border-top:1px solid {RULE};">{text}</h2>')


def h3(text):
    return f'<h3 style="font-family:{DISP}; font-size:1.15em; margin:24px 0 8px;">{text}</h3>'


def code(t):
    """Monospace comes from the browser; only the colour is worth a declaration."""
    return f'<code style="color:{QUILL};">{t}</code>'


def key(t):
    """A keyboard shortcut. A bordered box costs three declarations that treble in the
    editor's rewrite, so it is set in bold monospace instead."""
    return f'<code style="font-weight:700;">{t}</code>'


def term(t):
    return f"<em>{t}</em>"


def readout(lines):
    """A few lines as the game prints them - one box, however many lines."""
    inner = "<br>".join(lines)
    return (f'<div style="font-family:{MONO}; font-size:0.82em; color:{INK2}; background:{SURFACE};'
            f' border-left:3px solid {RULES}; padding:9px 12px; margin:12px 0; line-height:1.6;">'
            f'{inner}</div>')


def note(label, text):
    return (f'<div style="background:{SURFACE}; border-left:4px solid {QUILL}; padding:12px 16px;'
            f' margin:20px 0;">'
            f'<div style="font-family:{MONO}; font-size:0.68em; font-weight:700; letter-spacing:0.14em;'
            f' text-transform:uppercase; color:{QUILL}; margin-bottom:6px;">{label}</div>'
            f'<div>{text}</div></div>')


def bullets(items, mb="14px"):
    lis = "".join(f"<li>{i}</li>" for i in items)
    return (f'<ul style="margin:10px 0 {mb}; padding-left:22px; color:{INK2}; line-height:1.65;">'
            f"{lis}</ul>")


def specs(items):
    cells = []
    for n, unit, label in items:
        u = (f' <span style="font-size:0.5em; color:{INK3}; font-style:italic;">{unit}</span>'
             if unit else "")
        cells.append(
            f'<td style="padding:15px 14px; vertical-align:top; width:25%;">'
            f'<div style="font-family:{MONO}; font-size:1.8em; font-weight:600; line-height:1;">{n}{u}</div>'
            f'<div style="font-size:0.78em; color:{INK3}; margin-top:6px; line-height:1.4;">{label}</div></td>')
    return (f'<table style="width:100%; border-top:1px solid {RULES}; border-bottom:1px solid {RULES};'
            f' background:{SURFACE}; margin:0 0 28px; text-align:left;" cellspacing="0" cellpadding="0">'
            f'<tr>{"".join(cells)}</tr></table>')


def chips(words):
    return (f'<div style="font-family:{MONO}; font-size:0.78em; color:{INK3}; margin:14px 0 4px;">'
            + " &nbsp;&middot;&nbsp; ".join(words) + "</div>")


H = []
a = H.append

a(f'<div style="font-family:{BODY}; font-size:16px; line-height:1.75; color:{INK};'
  f' background:{PAPER}; max-width:860px; margin:0 auto; padding:30px 26px 34px;">')

# ---- masthead ----
a(f'<div style="font-family:{MONO}; font-size:0.68em; font-weight:600; letter-spacing:0.16em;'
  f' text-transform:uppercase; color:{QUILL}; margin-bottom:10px;">Vintage Story &middot; mod guide</div>')
a(f'<h1 style="font-family:{DISP}; font-size:2.9em; line-height:1; margin:0 0 12px;">'
  f'The Seraph&#39;s Ledger</h1>')
a(f'<p style="font-size:1.1em; line-height:1.6; color:{INK2}; max-width:34em; margin:0 0 18px;">'
  f'A bundle of small quality-of-life tweaks for Vintage Story &mdash; a trader-tracking ledger, '
  f'larger storage containers, inventory shortcuts, automatic enemy loot, ice-cooled cellars, a '
  f'builder&#39;s material swatch tool, a tally board for your pen, bigger firepit fuel stacks, a sortable browser for your '
  f'installed mods, and a few audio and interface tweaks.</p>')
a(f'<div style="font-family:{MONO}; font-size:0.76em; color:{INK3}; margin:0 0 26px;">v1.30.1 '
  f'&nbsp;&middot;&nbsp; built for Vintage Story 1.22 &nbsp;&middot;&nbsp; singleplayer or server</div>')

a(specs([("24", "switches", "features, each one on or off on its own"),
         ("100", "slots", "in a large trunk, made from two ordinary ones"),
         ("2&times;", "shelf life", "for food in a cellar stocked with ice"),
         ("0", "dependencies", "nothing else to install, nothing to configure")]))

a(p('Every feature here began as one small thing that was missing, and none of them is big enough '
    'to deserve a page of its own &mdash; so they are gathered into one mod instead. They do not '
    'depend on each other and they do not come as a set: a settings panel in game has a switch for '
    'each one, so you can run the whole bundle or just the part you came for.'))
a(p(f'Press {key("Ctrl+Shift+L")} or run {code(".slconfig")} to open that panel. Your choices are '
    f'written to {code("ModConfig/seraphsledger/features.json")}, and because these features are '
    f'wired up as the game loads, a change takes effect on the next restart. The panel says so.'))

# ---- what is in it ----
a(h2("What is in it"))
a(bullets([
    f'<b>Mod list browser</b> &mdash; {key("Ctrl+Shift+N")} or {code(".slmods")}',
    f'<b>Trader ledger</b> &mdash; automatic; {key("Ctrl+K")} or {code(".traders")}',
    '<b>Large storage containers</b> &mdash; crafted from two of the ordinary one',
    f'<b>Large container rescue</b> &mdash; automatic; {code("/sl rescue")}, {code("/sl status")}',
    '<b>Sort by freshness</b> &mdash; a button under the large vessel&#39;s grid',
    f'<b>Crate sweep</b> &mdash; {key("Ctrl+Shift")} + right-click a crate',
    f'<b>Move all of one item</b> &mdash; {key("Shift")} + middle mouse on a slot',
    f'<b>Ctrl + click move-all</b> &mdash; {key("Ctrl")} + left-click a slot',
    '<b>Sort and Trash buttons</b> &mdash; in the player inventory',
    f'<b>Animal tally board</b> &mdash; crafted and hung; {key("Ctrl+Shift+X")}, {code("/tally")}',
    '<b>Ice-cooled cellars</b> &mdash; stock a cellar with ice',
    f'<b>Material swatch tool</b> &mdash; {code("/sw mark")} two corners, then {key("Ctrl+Shift+M")}',
    '<b>Barrel ingredient portions</b> &mdash; automatic, at the barrel',
    '<b>Bigger fuel stacks</b> &mdash; automatic, at the firepit',
    '<b>Enemy fast-despawn and loot</b> &mdash; automatic, on death',
    '<b>Auto-loot all animals</b> &mdash; automatic, off by default',
    '<b>Creative search auto-focus</b> &mdash; opening the creative inventory',
    '<b>Silenced voices</b> &mdash; automatic',
    f'<b>Distance challenge</b> &mdash; {code("/steps")}, off by default']))

# ---- storage ----
a(h2("Storage"))

a(h3("Large storage containers"))
a(p('Bigger versions of the vanilla containers, each crafted from two of the ordinary one in a '
    'shapeless grid recipe. They keep the vanilla look, footprint and sounds.'))
a(bullets(["<b>Large Trunk</b> &mdash; 100 slots, from 2&times; Trunk",
           "<b>Large Chest</b> &mdash; 45 slots, from 2&times; Chest",
           "<b>Large Labeled Chest</b> &mdash; 45 slots, from 2&times; Labeled Chest",
           "<b>Large Storage Vessel</b> &mdash; 35 slots, from 2&times; Storage Vessel",
           "<b>Large Basket</b> &mdash; 21 slots, from 2&times; Basket"]))
a(note("Do not load that world without this mod",
       'A large container holds more slots than the vanilla block entity defaults to, and that count '
       'lives in the block &mdash; so a save loaded <em>once</em> without this mod trims every large '
       'container it touches to 16 slots and drops the rest for good. No error, no log line, just an '
       'emptier trunk. The game does that itself, before any mod gets a say, and it is the same for '
       'every mod that widens a container. The rescue below is the net under it, and it has to be '
       'installed <em>before</em> the loss to catch anything.'))

a(h3("Large container rescue"))
a(p(f'The mod keeps a copy of each large container&#39;s contents in the savegame&#39;s own mod data '
    f'&mdash; which the game loads and writes back untouched for mods that are not installed, so the '
    f'record survives exactly the thing that destroys the container. When one comes back from disk '
    f'short of what the record says it held, the items go straight back in and you get a chat line '
    f'saying so. That happens only when the shortfall carries the engine&#39;s own fingerprint: '
    f'nothing left at or past slot 16, where the trimming always cuts. A container merely emptier than '
    f'the record &mdash; which is what your own hand looks like &mdash; is held for you to decide: '
    f'{code("/sl rescue")} lists it, {code("/sl rescue 2")} puts it back, {code("/sl forget 2")} drops '
    f'it, and {code("/sl status")} says what is on record against what it can see now. Restoring only '
    f'ever fills empty slots, matching is by contents rather than by slot so sorting is never mistaken '
    f'for a loss, and nothing trimmed before version 1.25.0 can be recovered &mdash; there was no '
    f'record to keep.'))

a(h3("Sort by freshness"))
a(p('A vessel full of crocks tells you nothing about which one to open next. The <b>Sort by '
    'freshness</b> button under the Large Storage Vessel&#39;s grid lays the whole vessel out as a '
    'larder, in the order you ought to eat it: unsealed crocks first, then sealed ones, then empty '
    'ones, then the loose food &mdash; fruit, vegetables, eggs, grain, flour, bread, cheese, meat, a '
    'bowl of a meal &mdash; in one queue, then the food that keeps, then everything else by item. '
    'Within each group whatever goes off soonest comes first, by the same "fresh for" figure the '
    'item&#39;s own tooltip shows, with the seal and the vessel&#39;s (or an ice-chilled cellar&#39;s) '
    'perish rate already counted in.'))
a(p('Part-stacks are merged the game&#39;s own way, so perishables average their freshness instead of '
    'one stack quietly inheriting the other&#39;s timer. The sort runs on the server for the player '
    'who has that vessel open, and reads the food off a copy so the vessel keeps ticking at its own '
    'rate. Hovering the button explains the order, and that hover text has its own switch.'))

a(h3("Crate sweep"))
a(p('One gesture puts <em>every</em> stack of what a crate holds into it, taken from your hotbar and '
    'your worn bags. A crate takes one kind of thing and names that kind itself, so a sweep can never '
    'put the wrong item in &mdash; which makes a crate the one container where "all of this, in there" '
    'is safe to ask for. Three ways to ask, each with its own switch:'))
a(bullets([
    f'{key("Ctrl+Shift")} + <b>right-click with an empty hand</b> reads the kind off the crate&#39;s '
    f'own contents. Vanilla does nothing at all for that gesture, so this fills a gap rather than '
    f'changing an interaction.',
    f'{key("Ctrl+Shift")} + <b>right-click holding the item</b> is vanilla&#39;s "add all items"; the '
    f'sweep carries on past the held stack and empties the rest. This one does change a vanilla '
    f'interaction, so it can be turned off on its own.',
    f'{key("Shift")} + <b>middle mouse</b> &mdash; the move-all-of-one-item binding &mdash; aimed at a '
    f'crate sweeps it, so the shortcut means the same thing at a slot or at a block.']))
a(p('An empty crate names no kind, so a sweep leaves it alone: put the first stack in and the sweep '
    'follows it. Retrieve-only crates are never swept into, and when a crate fills up partway through, '
    'the leftovers stay in your hotbar rather than in a bag.'))

a(h3("Inventory shortcuts"))
a(p(f'Hold {key("Shift")} and press the <b>middle mouse button</b> on a slot and every stack of that '
    f'same item leaves that inventory &mdash; all your flax, none of your ore. It rides the '
    f'engine&#39;s own shift-click transfer, so each stack lands exactly where shift-clicking it would '
    f'have put it. It matches the item rather than the stack, so a mixed-wear pile of copper axes moves '
    f'as one, and unlike Ctrl + click move-all it needs no container open: with just your inventory up '
    f'it gathers an item between hotbar and backpack. Rebindable under '
    f'{term("Controls &rarr; Inventory hotkeys")} to any mouse button or key.'))
a(p(f'Beside it: {key("Ctrl")} + <b>left-click</b> any slot to dump every non-empty slot of that '
    f'inventory into the open container, a <b>Sort</b> button in the player inventory for a '
    f'server-authoritative sort of your worn bags, and a <b>Trash</b> button that destroys whatever is '
    f'on the cursor.'))

# ---- the farm and the cellar ----
a(h2("The farm and the cellar"))

a(h3("Animal tally board"))
a(p('A slate chalkboard, hung on a wall like a painting, that keeps count of the animals in its pen. '
    'Look at it and the tooltip reads out what is inside the fence: each species broken down by kind, '
    'how many females are pregnant, the eggs in henboxes and how many of those are fertile, and any '
    'eggs lying loose on the floor. Crafted from <b>2 planks and 1 charcoal</b>.'))
a(readout(["Pen closed (412 blocks)",
           "Chicken: 27 &nbsp;&middot;&nbsp; Hen 15 &middot; Rooster 9 &middot; Chick 3",
           "Pregnant: 2 &nbsp;&middot;&nbsp; Sheep 2",
           "Ready to milk: 3 &nbsp;&middot;&nbsp; Sheep 3",
           "Eggs: 6 in henboxes (4 fertile) &middot; 2 loose"]))
a(p('It counts the pen, not a circle around itself: the board walks the ground outward the way an '
    'animal could, so fences, walls, closed gates and closed doors stop it while open doorways do not, '
    'and coops joined to the pen are counted as one. A neighbour&#39;s flock on the other side of the '
    'fence never lands in your total. If that walk escapes and runs 100 blocks out, the pen is not '
    'closed &mdash; the board says so in orange, and right-clicking it marks the one block that would '
    'close the gap. The count is taken on the server every 10 seconds, only while somebody is within '
    '64 blocks, and only the totals are sent out.'))
a(p(f'The walk has to guess where a pen ends, and an open gate or a wide doorway is enough to defeat '
    f'it, so a board can be told instead: {code("/tally mark")} one corner, {code("/tally mark")} the '
    f'opposite one, then sneak + right-click a board to bind it. Marked corners glow while you work, '
    f'and two corners at the same height grow a box up to whatever stands in the footprint and down to '
    f'the ground beneath it, so you walk a pen at ground level rather than flying up to the fence tops. '
    f'{key("Ctrl+Shift+X")} lights up what a board is counting. A bound board counts a box, honestly '
    f'rather than cleverly: an L-shaped pen will include the notch outside the fence. Leave it unbound '
    f'for the walk.'))
a(p('The board also counts what is <b>ready to milk</b>: lactating, and not milked in the last day. '
    'Stress is left out of that on purpose &mdash; it lapses by itself within a minute, and counting it '
    'would only make the number flicker while a herd settles. Switch on <b>Milk-ready markers</b> and '
    'every ready animal wears a small milk-white plume while you stand in the pen that board counts; '
    'milk one and its marker goes at once. It is a plume rather than an outline round the animal '
    'because the game offers shaded highlights for blocks and for nothing else, and faking one means '
    're-rendering the animal&#39;s mesh through a render patch that breaks on engine updates &mdash; a '
    'poor thing to keep repairing for a cosmetic. The markers are off by default.'))

a(h3("Ice-cooled cellars"))
a(p('Keep your food twice as long by stocking your cellar with ice. Place blocks of ice &mdash; '
    'glacier ice, packed glacier ice, anything icy &mdash; in a proper cellar and the room chills below '
    'the vanilla 5&deg;C cellar floor. The effect starts at <b>4 blocks</b> and maxes out at <b>10</b>, '
    'where the cellar behaves as if it were about &minus;3&deg;C, roughly double the shelf life of a '
    'perfect vanilla cellar. Nothing freezes; food just keeps longer. Ice counts stacked inside the room '
    'or lining the walls, floor or ceiling, and an ice wall rates like stone or soil for the cellar '
    'bonus rather than like wood, as vanilla has it.'))
a(p('Cellar ice melts at the temperature it made rather than at outdoor-summer speed: a fully chilled '
    'cellar loses roughly one block per 17 hours of play, faster as the stock dwindles, and nothing at '
    'all in winter. Melting indoors leaves no water behind, and fully buried ice never melts. Hover any '
    'container in the room to see the ice count, the chill strength and the perish speed it is buying '
    'you. The room has to qualify as a vanilla cellar, and the thresholds, target temperature and melt '
    'rate are all tunable in the config.'))
a(note("Running the standalone Ice Cellar mod too",
       'This feature also ships on its own as <b>Ice Cellar</b>, for anyone who wants the mechanic and '
       'none of the rest of the bundle. Running both is safe from Seraph&#39;s Ledger <b>1.15.2</b> '
       'onward: the Ledger notices Ice Cellar and hands the feature over, so nothing is applied twice. '
       'On <b>1.15.1</b> or older, update or switch ice-cooled cellars off here.'))

a(h3("Barrel ingredient portions"))
a(p('Barrel recipes are exact ratios, not "up to". A full 50&nbsp;L barrel of water wants exactly '
    '<b>50 lime</b> for limewater and exactly <b>5 oak or acacia logs</b> for weak tannin &mdash; tip a '
    'whole stack in and vanilla swallows the lot, then matches no recipe at all until you work out how '
    'much to pull back out. With this on, the barrel takes what the liquid in it can actually use and '
    'the remainder stays in your inventory. The amount comes from the recipes themselves, so strong '
    'tannin stops at 25 small hides and recipes added by other mods are portioned too, however you load '
    'the barrel &mdash; click, shift-click, move-all or a hopper. An empty barrel is left alone, since '
    'the liquid is what fixes the ratio.'))

a(h3("Bigger fuel stacks"))
a(p('Firewood, aged firewood and peat bricks stack to <b>64</b> instead of 32, so one load keeps a '
    'firepit going about 26 minutes rather than 13. A firepit never had a fuel limit of its own: its '
    'fuel slot takes almost any number and it burns one item at a time, so what capped a load was the '
    'fuel&#39;s own stack size. Raising that raises it everywhere &mdash; bags, chests, crates. '
    'Firewood piles on the ground are unchanged, since that number belongs to the ground-storage '
    'behaviour rather than to the item.'))
a(p('Which codes count as fuel is yours in features.json, matched on the first part of the code so '
    'every variant follows and a modded firewood is raised beside the vanilla one, and a listed code '
    'still has to actually burn before anything is changed. The size accepts 32 to 999, and only fuels '
    'below it are touched, so the default of 64 leaves charcoal, coal and coke at vanilla. On a server '
    'it is the server&#39;s setting that applies: it sends its own stack sizes to every client on '
    'connect.'))

# ---- building and trading ----
a(h2("Building and trading"))

a(h3("Material swatch tool"))
a(p(f'See a build in a different material before you commit to it. Aim at one corner and run '
    f'{code("/sw mark")}, aim at the opposite corner and mark again &mdash; two marks at the same '
    f'height read as a footprint and the top comes off the rain map, so a whole building is bracketed '
    f'without flying up to its roof, and the area is highlighted so you can see what you caught. Press '
    f'{key("Ctrl+Shift+M")}: the materials in the selection on the left, what each can become on the '
    f'right with the number of blocks each swap would change. <b>Apply</b>, and <b>Undo</b> if you do '
    f'not like it.'))
a(bullets([
    'Every finish of a material moves at once &mdash; cobble, bricks, polished, stairs and slabs '
    '&mdash; and stair facings survive. The choices come from the block registry rather than a '
    'built-in list, so modded stone and wood show up on their own.',
    'Only blocks somebody placed are listed or swapped. Grass, soil, gravel, ore, raw stone, water and '
    'plant life stay out of it, so restyling a wall never repaints the hillside behind it.',
    'Chiselled blocks are re-materialed in place, keeping their carving. Chests and doors are skipped '
    'and reported rather than swapped and emptied.',
    f'Undo covers your last 25 swaps. Chat equivalents: {code("/sw list")}, {code("/sw options")}, '
    f'{code("/sw swap")}, {code("/sw undo")}, {code("/sw unmark")}. Needs the '
    f'{code("controlserver")} privilege.']))

a(h3("Trader ledger"))
a(p(f'Every trader whose Buy/Sell window you open is written down &mdash; location, name, type, and the '
    f'full buy and sell lists with prices and stock as of your last visit. Stored per world, and '
    f'revisiting a trader updates the entry in place. A waypoint colour-coded by trader type is dropped '
    f'at each one the first time you visit ({code(".traders wp")} maps any that were missed). '
    f'{key("Ctrl+K")} opens the browser: search by name, type or item, sort by type, name or most '
    f'recent, and click <b>[show on map]</b> to jump to one. In chat, {code(".traders")} lists everyone, '
    f'{code(".traders &lt;text&gt;")} shows full wares for matches, and {code(".traders export")} writes '
    f'a Markdown summary to the mod data folder.'))

# ---- combat ----
a(h2("Combat and loot"))
a(p(f'<b>Hostile creatures vanish 2 seconds after death and drop their loot automatically</b> &mdash; no '
    f'knife-harvesting required. That covers anything in the vanilla {code("hostile")} spawn group '
    f'(drifters, locusts, bells, shivers, bowtorn, and hostile animals like wolves, bears and hyenas), '
    f'modded mobs using that group, and their young, which carry no spawn rule of their own and are '
    f'recognised by family instead. Drops match a full harvest, scaled by {code("animalLootDropRate")} '
    f'for anything that is not mechanical, and harvesting the body yourself inside those 2 seconds never '
    f'duplicates the loot.'))
a(p(f'<b>Auto-loot all animals</b> is the same thing for everything else you kill &mdash; deer, sheep, '
    f'pigs, hares &mdash; and is off by default. An animal&#39;s body is not deleted: it is handed to '
    f'the game&#39;s own dead-decay behaviour, so it leaves the carcass block it would have decayed '
    f'into. Switch "auto-loot leaves a carcass" off and auto-looted animals are removed the way hostiles '
    f'are, which is what you want for a cull &mdash; the carcass block is not replaceable, so two '
    f'hundred culled pigs otherwise carpet the floor with blocks to break one at a time. The delay is '
    f'yours ({code("AutoLootDelaySeconds")}, 7 by default) rather than the hostiles&#39; fixed 2 '
    f'seconds, because 2 seconds is not long enough to change your mind about a kill.'))
a(p('Two things are deliberately left alone. If anyone has the carcass window open when the timer fires, '
    'auto-loot skips that body completely &mdash; emptying an inventory out from under an open window is '
    'exactly how you get items that look like they come back. And an animal that died without a killer, '
    'by falling or drowning or losing a fight with a predator, is left standing to be harvested by hand: '
    'generating harvest drops needs the player who landed the kill, so with no such player the body '
    'would otherwise be removed with its loot still inside it.'))

# ---- interface ----
a(h2("The interface"))

a(h3("Mod list browser"))
a(p(f'{key("Ctrl+Shift+N")} or {code(".slmods")} opens a sortable list of every mod you have installed. '
    f'The game&#39;s own mod manager lists them in file-name order and nothing else; this one sorts by '
    f'install date, declared game version, available update, load order, load failures, enabled state, '
    f'author or description, either direction, and highlights the value it sorted by on each row. The '
    f'search box filters on name, mod id, file name, author and description.'))
a(bullets([
    'Each row carries an <b>[enable]</b> / <b>[disable]</b> link &mdash; the one part of the game&#39;s '
    'mod manager reachable from inside a world. It takes a restart either way, so a row you changed this '
    'session reads <b>[enabled on restart]</b> and the summary counts how many are waiting.',
    'Disabling asks twice and says what it will cost: the mods that depend on this one, a warning if it '
    'declares itself part of the base game, and a reminder that its blocks read as unknown in worlds '
    'built with it. Enabling takes one click, since nothing is lost that way.',
    '<b>Rescan</b> re-reads the mod folders without a restart. <b>Check updates</b> asks the mod database '
    'for the newest release tagged for this game&#39;s version series and flags what you are behind on '
    '&mdash; and admits when a local dev build is ahead of anything published.',
    'Installing and deleting mods are left out on purpose: those move files the running game is reading.']))

a(h3("The small ones"))
a(p(f'<b>Creative search auto-focus</b> puts the cursor in the search box the moment the creative '
    f'inventory opens, so you can start typing (while it is focused the E key types instead of closing '
    f'the dialog &mdash; close with Escape or the X). <b>Silenced voices</b> mutes the chattering '
    f'character sounds, both the player&#39;s seraph and the trader greeting. <b>Distance challenge</b>, '
    f'off by default, is a HUD travel budget that drains as you walk and stops you dead at zero; '
    f'{code("/steps add")} and {code("/steps set")} adjust it.'))

# ---- switches and installing ----
a(h2("Switches, and installing"))
a(p(f'{key("Ctrl+Shift+L")} or {code(".slconfig")} opens the settings panel: one switch per feature, in '
    f'two columns. There is one for each of the nineteen features above, plus five more for the parts '
    f'you might want on their own &mdash; the three crate sweep gestures separately, the hover text on '
    f'the vessel&#39;s sort button, whether auto-loot leaves a carcass, and the milk-ready markers '
    f'&mdash; twenty-four in all. '
    f'Every key named on this page is rebindable in {term("Settings &rarr; Controls")}, where the '
    f'move-all binding sits under Inventory hotkeys, and most features have a chat command beside the '
    f'key for when a binding is already spoken for.'))
a(p('Drop the zip in your Mods folder &mdash; %appdata%/VintagestoryData/Mods on Windows, '
    '~/.config/VintagestoryData/Mods on Linux &mdash; and start the game. On a server, install it on the '
    'server as well: the loot, container, trader and tally features all have a server side, so a '
    'client-only install leaves most of the bundle doing nothing. Then switch off whatever you did not '
    'come for, and restart once.'))
a(chips(["no dependencies", "singleplayer or server", "every feature optional",
         "built for Vintage Story 1.22"]))

# ---- footer + disclosure ----
a(f'<div style="margin-top:32px; padding-top:16px; border-top:1px solid {RULES};">')
a(f'<p style="font-family:{MONO}; font-size:0.74em; color:{INK3}; margin:0 0 12px;">'
  f'The Seraph&#39;s Ledger v1.30.1 &middot; built for Vintage Story 1.22</p>')
a(f'<p style="font-size:0.88em; line-height:1.7; color:{INK3}; margin:0;">'
  f'AI Disclosure: This mod was developed with the assistance of AI tools, and its full source code is '
  f'linked on this page for anyone who wants to review it. If AI-assisted content isn&#39;t for you, please '
  f'filter or skip this mod. The comment section is reserved for bug reports, compatibility questions, and '
  f'gameplay feedback.</p>')
a("</div>")
a("</div>")

fragment = "\n".join(H)

# Whitespace inside a style attribute is only there for the reader of this script, and it is
# counted twice over once the site's editor rewrites the page. Nothing outside style="..." is
# touched, so the prose keeps its own semicolons and colons.
fragment = re.sub(r'style="([^"]*)"',
                  lambda m: 'style="' + re.sub(r"\s*([:;])\s*", r"\1", m.group(1)).rstrip(";") + '"',
                  fragment)

here = os.path.dirname(os.path.abspath(__file__))

moddb = os.path.join(here, "seraphsledger-moddb.html")
with open(moddb, "w") as f:
    f.write(fragment)

page = os.path.join(here, "seraphsledger.html")
with open(page, "w") as f:
    f.write("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n"
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
            "<title>The Seraph's Ledger</title>\n<!-- Generated by docs/build-moddb-description.py "
            "- do not edit; edit the script. -->\n</head>\n"
            f'<body style="margin:0; background:{PAPER};">\n' + fragment + "\n</body>\n</html>\n")

# What the site measures is this file after its editor has expanded every shorthand and turned
# every #hex into rgb(r, g, b). Estimate that here rather than finding out on upload.
decls = sum(len([d for d in s.split(";") if d.strip()])
            for s in re.findall(r'style="([^"]*)"', fragment))
hexes = len(re.findall(r"#[0-9A-Fa-f]{6}", fragment))
borders = len(re.findall(r"border[a-z-]*:[^;\"]+", fragment))
pasted = len(fragment) + decls * 3 + hexes * 11 + borders * 60

print(f"written {moddb} {len(fragment)} bytes ({len(fragment) / 1024:.1f}KB)")
print(f"written {page}")
print(f"estimated once pasted: ~{pasted / 1000:.0f}KB - the ModDB counts that, not the file")
if len(fragment) > BUDGET:
    print(f"WARNING: over the {BUDGET // 1024}KB budget by {(len(fragment) - BUDGET) / 1024:.1f}KB. "
          f"The site rejected 53KB here as 85KB there. Drop a whole aside, never add a table.")
