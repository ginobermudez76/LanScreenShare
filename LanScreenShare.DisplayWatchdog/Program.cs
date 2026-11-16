using System.Threading;
using LanScreenShare.Core.Display;
using LanScreenShare.Media.Display;

if (!VirtualDisplayWatchdogRunner.TryParseArgs(args, out var options))
    return 1;

IVirtualDisplayBackend[] backends =
[
    new VirtualDriversVddBackend(),
    new ParsecVddBackend()
];

var topologyManager = new WindowsDisplayTopologyManager();
var recoveryContext = new WatchdogRecoveryContext(backends, topologyManager);
await VirtualDisplayWatchdogRunner.WaitAndRecoverAsync(
    options,
    VirtualDisplayWatchdogRunner.WaitForProcessIdExitAsync,
    recoveryContext,
    CancellationToken.None);

return 0;
