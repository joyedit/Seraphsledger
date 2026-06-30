# Character voice ("vocals") — silenced

The seraph/NPC talking blips are the **voice type** sounds. Vanilla ships 8,
named after instruments, in the `game` domain at `assets/game/sounds/voice/`.

This mod replaces **all 8** with a 0.1 s silent mono Vorbis `.ogg`, so no matter
which `voicetype` a character has selected, the talking/emote/hurt/death
vocalizations are inaudible.

| voicetype code | file overridden (now silent)             |
|----------------|------------------------------------------|
| altoflute      | `assets/game/sounds/voice/altoflute.ogg` |
| harmonica      | `assets/game/sounds/voice/harmonica.ogg` |
| oboe           | `assets/game/sounds/voice/oboe.ogg`      |
| clarinet       | `assets/game/sounds/voice/clarinet.ogg`  |
| accordion      | `assets/game/sounds/voice/accordion.ogg` |
| trumpet        | `assets/game/sounds/voice/trumpet.ogg`   |
| sax            | `assets/game/sounds/voice/sax.ogg`       |
| tuba           | `assets/game/sounds/voice/tuba.ogg`      |

## How the override works

The mod ships these files under `assets/game/...` (the **game** domain, not
`seraphsledger`). Vintage Story loads mod assets after the base game, so a file whose
domain + path matches a vanilla asset replaces it. No code or JSON edits.

The engine still runs its `EntityTalkUtil` synthesis (pitch glide, vibrato, etc.)
per `EnumTalkType`, but since the source sample is silence, nothing is heard.

## To regenerate the silent files

```sh
cd assets/game/sounds/voice
ffmpeg -y -f lavfi -i anullsrc=r=44100:cl=mono -t 0.1 -c:a libvorbis silence.ogg
for n in altoflute harmonica oboe clarinet accordion trumpet sax tuba; do
  cp silence.ogg "$n.ogg"
done
rm silence.ogg
```

## To use a real custom voice instead

Replace any of the files above with a short (~0.3–1.0 s), tonal/sustained **mono**
`.ogg`. The engine slices and re-pitches it per syllable, so a held note works
best; long or percussive clips get chopped.
