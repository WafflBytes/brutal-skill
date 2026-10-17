BrutalGame3D
========

Roll the ball around the arena and collect all 13 coins before the 60-second timer runs out.
Some coins are up the ramps and on the raised platforms.

How to play
-----------
  W A S D or arrow keys   roll (relative to the camera)
  Q / E                   turn the camera
  P                       pause (the game also pauses when its window loses focus)
  R                       restart the round
  Enter                   play again after a round ends
  Esc                     quit
  Enter, Space, click     skip the logo screen

  Gamepad: left stick rolls, right stick turns the camera, A = confirm, Start = pause,
  Back/View = restart.

Running it
----------
Unzip the whole folder somewhere and double-click BrutalGame3D.exe. Keep the other files next to it.

It needs Windows 10 or 11 (64-bit), a graphics driver with OpenGL 4.1, and the Microsoft Visual C++
Redistributable (x64), which most PCs already have. If it's missing, the game says so and links to
the official download: https://aka.ms/vs/17/release/vc_redist.x64.exe

Windows may show "Windows protected your PC" (SmartScreen) because the game isn't signed by a
publisher. Click "More info", then "Run anyway".

If the game ever stops with an error, the details are saved to
%LOCALAPPDATA%\BrutalGame3D\error.log

Credits and licences
--------------------
Built with BRUTAL. BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited,
used under the BRUTAL License v1.0 (see NOTICE.txt).

Physics by Jolt Physics. Window, input and sound by SDL3 and SDL_mixer; text by SDL_ttf.

Font: Atkinson Hyperlegible, Copyright 2020 Braille Institute of America, Inc., licensed under the
SIL Open Font License 1.1 (see OFL-AtkinsonHyperlegible.txt).

Portions of this software are copyright (c) 2023 The FreeType Project (https://freetype.org).
All rights reserved. This software is based in part on the work of the FreeType Team.

Other third-party notices (Jolt, SDL3, SDL_ttf, HarfBuzz, SDL_mixer, GLAD, stb, the .NET runtime)
are in the licenses folder.
