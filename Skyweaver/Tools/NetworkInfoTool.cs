using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Skyweaver.Controls.ChatSessionControl.Views;
using Skyweaver.Services.SkyweaverTools;

namespace Skyweaver.Tools
{
    public sealed class NetworkInfoTool :
        ISkyweaverTool,
        ISkyweaverToolInvocationPresentationProvider,
        ISkyweaverToolPromptDescriptionProvider
    {
        public const string ToolName = "NetworkInfo";

        private static readonly SkyweaverToolDefinition s_definition = new(
            ToolName,
            "Retrieves detailed network information including active interfaces and IP addresses.",
            "Device",
            [],
            defaultAgentPermission: SkyweaverToolDefaultAgentPermission.Allow);

        public SkyweaverToolDefinition Definition => s_definition;

        public string GetPromptDescription(SkyweaverToolPromptDescriptionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return "Retrieves detailed network information including active network interfaces, IP addresses, and DNS servers. Useful for network diagnostics.";
        }

        public FrameworkElement? CreateInvocationPresentation(SkyweaverToolInvocationPresentationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ToolInvocationCardFactory.Create(context, []);
        }

        public Task<SkyweaverToolResult> ExecuteAsync(
            SkyweaverToolContext context,
            SkyweaverToolArguments arguments,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var builder = new StringBuilder();
                builder.AppendLine("=== Network Information ===");

                builder.AppendLine($"Host Name: {Dns.GetHostName()}");

                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var adapter in interfaces)
                {
                    if (adapter.OperationalStatus == OperationalStatus.Up)
                    {
                        builder.AppendLine($"\nInterface: {adapter.Name}");
                        builder.AppendLine($"  Description: {adapter.Description}");
                        builder.AppendLine($"  Type: {adapter.NetworkInterfaceType}");
                        builder.AppendLine($"  MAC Address: {adapter.GetPhysicalAddress()}");

                        var properties = adapter.GetIPProperties();
                        foreach (var ip in properties.UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                builder.AppendLine($"  IPv4 Address: {ip.Address}");
                                builder.AppendLine($"  Subnet Mask: {ip.IPv4Mask}");
                            }
                            else if (ip.Address.AddressFamily == AddressFamily.InterNetworkV6)
                            {
                                builder.AppendLine($"  IPv6 Address: {ip.Address}");
                            }
                        }

                        foreach (var dns in properties.DnsAddresses)
                        {
                            builder.AppendLine($"  DNS Server: {dns}");
                        }

                        foreach (var gateway in properties.GatewayAddresses)
                        {
                            builder.AppendLine($"  Gateway: {gateway.Address}");
                        }
                    }
                }

                return Task.FromResult(SkyweaverToolResult.Success(builder.ToString().TrimEnd()));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"NetworkInfoTool execution failed: {ex}");
                return Task.FromResult(SkyweaverToolResult.Failure($"Failed to retrieve network info: {ex.Message}"));
            }
        }
    }
}
