WARNING: This dir contains 100% certified AI slop, no human eyes ever seen this during development.


This sample runs a simple wayland compositor that presents created windows as controls embedded into Avalonia application.

The sample is incomplete and is mostly here to smoke-test the server code for compatibility with real-world apps, e. g. it supports gedit and you can actually type there.
To run an app with subcompositor use `XDG_SESSION_TYPE=wayland DISPLAY= WAYLAND_DISPLAY=<socket-path-the-printed-or-one-youve-specified-as-its-first-arg> your-app`.
