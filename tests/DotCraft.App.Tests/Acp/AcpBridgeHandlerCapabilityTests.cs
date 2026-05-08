using DotCraft.Acp;

namespace DotCraft.Tests.Acp;

/// <summary>
/// Unit tests for <see cref="AcpBridgeHandler"/> capability mapping (ACP client → wire <c>acpExtensions</c>).
/// </summary>
public sealed class AcpBridgeHandlerCapabilityTests
{
    [Fact]
    public void BuildAcpExtensionCapability_MapsDeclaredCapabilities()
    {
        var caps = new ClientCapabilities
        {
            Fs = new FsCapabilities
            {
                ReadTextFile = true,
                WriteTextFile = true
            },
            Terminal = new TerminalCapabilities { Create = true },
            Extensions = ["_unity", "foo"]
        };

        var ext = AcpBridgeHandler.BuildAcpExtensionCapability(caps);

        Assert.NotNull(ext);
        Assert.True(ext!.FsReadTextFile);
        Assert.True(ext.FsWriteTextFile);
        Assert.True(ext.TerminalCreate);
        Assert.Equal(["_unity", "foo"], ext.Extensions);
    }
}
