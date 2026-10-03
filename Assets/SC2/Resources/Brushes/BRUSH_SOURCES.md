# Built-in brush images

16 grayscale stamps (white = full strength, black = nothing), 512x512, used by the Paint Blend and Geometry brushes.

## Cut from CC0 height maps (6)

`hill_round`, `hills_massif`, `hills_spur`, `ridge_cluster`, `ridge_spine`, `twin_peaks`

Source: Motion Forge Pictures, "Small Ridge Through Terrain" and "Rolling Hills Height Map 1k"
(https://www.motionforgepictures.com/height-maps/), released under CC0 1.0 Universal.
Each stamp is a window of one of the two 1025x1025 height maps with its base level removed, scaled to full range and
faded to black towards the edge.

## Generated for this project (10)

`massif_a` .. `massif_e`: value noise and ridged noise under a radial falloff.
`eroded_ridges_a` .. `eroded_ridges_e`: a dome with ridged noise run through a stream-power erosion simulation, the ridge lines
brightened and the whole faded to black towards the edge.

No third-party content in these.
