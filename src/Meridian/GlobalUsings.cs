// UseWindowsForms would add these two, but it needs a -windows target framework, which a dotnet
// tool cannot have. See Meridian.csproj.
global using System.Drawing;
global using System.Windows.Forms;
global using System.Diagnostics;
global using System.Runtime.InteropServices;
global using static NativeMethods;
global using Timer = System.Windows.Forms.Timer;
