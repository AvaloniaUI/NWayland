using System.Reflection;
using NWayland.Protocols.Wayland;

namespace HelloWayland;
class Program
{
    static void Main(string[] args)
    {
        var display = WlDisplay.Connect();
        var registry = display.GetRegistry(new WlRegistry.Listener.Relay
        {
            OnGlobal = ((sender, name, iface, version) =>
            {
                if (iface == "wl_seat")
                    WlSeat.Bind(sender, name, 7, new WlSeat.Listener.Relay
                    {
                        OnName = (eventSender, s) =>
                        {
                            Console.WriteLine("Got seat with name " + s);
                        }
                    });
                Console.WriteLine($"Got interface {iface} {version}");
            })
        });
        while (true)
        {
            display.Flush();
            display.Dispatch();
            Thread.Sleep(1);
        }
    }
}