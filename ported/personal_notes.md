# Problems

1. random explosions: spin, low grain, piercing rod, high speed. I don't have a repro so I can only describe what I'm seeing: based on the exact position and moment of the impact the behaviours differ drammatically and that's okay, the problem is that in some specific scenarios the damage done seems to be much higher than in others, in particular there seems to be a literal internal explosion on the far side of the impact that makes cells burst out at high speeds and overall comminutes lots of cells and greatly fractures the spinning body. In one instance the impact made the body reverse its spin direction.

2. bodies visually twitching in collide scene at default parameters. Might be a visual bug but on collision the bodies instantly move as if adjusting their position after one of their cells comminutes.

3. need more velocity injection in slow impacts (e.g. collide scene), to achieve more fracturing. Is it realistic that more speed (keeping all other parameters the same) makes the impactor comminute more and consequently  dealing less damage? Crush calculation may need to be revised (carving system works fine, what feeds it seems to be the problem).
The overlap depth factor (confinement) maybe should grow exponentially as it approaches the cap, so very light overlaps are resolved by pushing without carving anything.
Need a hard guarantee that deep overlap is impossible.
Need a hard guarantee that cells and fragments cannot detach inside the parent's body, must be somehow connected to the real surface.

4. design and implement joint mechanism.

5. need material parameters exposed in a single file so I can tune them. Add grain size and shear strength to materials.

6. ablation pass to clean the project and remove irrelevant things. simplify model and add updated documentation.

7. optimisation pass.

8. add weapon types only when the model is stable.