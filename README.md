# NWayland
.NET Bindings for Wayland with focus on correctness and anti shoot-yourself-in-the-foot guards. Multithreading is supported through wl_queue.

Client library used libwayland-client. Server library is 100% managed implementation since the only reason for using libwayland-server from C# code got deprecated and removed from MESA a while ago.

### Usage
Install the NuGet package `NWayland` and optionally `NWayland.Protocol.Plasma` and `NWayland.Protocol.Wlr` for Plasma or Wlroots-based compositors, respectively, in order to use their specific protocols. Server implementation is in NWayland.Server, see relevant [README](./src/NWayland.Server/README.md).

### Examples
See [samples](./samples)

### License
NWayland and its related components are licensed under the [MIT license](https://github.com/kekekeks/NWayland/blob/master/licence.md)
