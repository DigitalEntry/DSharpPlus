using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Net;
using DSharpPlus.VoiceNext.Entities;
using Newtonsoft.Json;

namespace DSharpPlus.VoiceNext;

/// <summary>
/// Represents VoiceNext extension, which acts as Discord voice client.
/// </summary>
public sealed class VoiceNextExtension : BaseExtension
{
    private VoiceNextConfiguration Configuration { get; set; }

    private ConcurrentDictionary<ulong, VoiceNextConnection> ActiveConnections { get; set; }
    private ConcurrentDictionary<ulong, TaskCompletionSource<VoiceStateUpdateEventArgs>> VoiceStateUpdates { get; set; }
    private ConcurrentDictionary<ulong, TaskCompletionSource<VoiceServerUpdateEventArgs>> VoiceServerUpdates { get; set; }

    /// <summary>
    /// Gets whether this connection has incoming voice enabled.
    /// </summary>
    public bool IsIncomingEnabled { get; }

    internal VoiceNextExtension(VoiceNextConfiguration config)
    {
        this.Configuration = new VoiceNextConfiguration(config);
        this.IsIncomingEnabled = config.EnableIncoming;

        this.ActiveConnections = new ConcurrentDictionary<ulong, VoiceNextConnection>();
        this.VoiceStateUpdates = new ConcurrentDictionary<ulong, TaskCompletionSource<VoiceStateUpdateEventArgs>>();
        this.VoiceServerUpdates = new ConcurrentDictionary<ulong, TaskCompletionSource<VoiceServerUpdateEventArgs>>();
    }

    /// <summary>
    /// DO NOT USE THIS MANUALLY.
    /// </summary>
    /// <param name="client">DO NOT USE THIS MANUALLY.</param>
    /// <exception cref="InvalidOperationException"/>
    protected internal override void Setup(DiscordClient client)
    {
        if (this.Client != null)
        {
            throw new InvalidOperationException("What did I tell you?");
        }

        this.Client = client;

        this.Client.VoiceStateUpdated += this.Client_VoiceStateUpdate;
        this.Client.VoiceServerUpdated += this.Client_VoiceServerUpdate;
    }

    /// <summary>
    /// Create a VoiceNext connection for the specified channel.
    /// </summary>
    /// <param name="channel">Channel to connect to.</param>
    /// <returns>VoiceNext connection for this channel.</returns>
    public async Task<VoiceNextConnection> ConnectAsync(DiscordChannel channel)
    {
        if (channel.Type != ChannelType.Voice && channel.Type != ChannelType.Stage)
        {
            throw new ArgumentException(nameof(channel), "Invalid channel specified; needs to be voice or stage channel");
        }

        if (channel.Guild == null)
        {
            throw new ArgumentException(nameof(channel), "Invalid channel specified; needs to be guild channel");
        }

        if (!channel.PermissionsFor(channel.Guild.CurrentMember).HasPermission(Permissions.AccessChannels | Permissions.UseVoice))
        {
            throw new InvalidOperationException("You need AccessChannels and UseVoice permission to connect to this voice channel");
        }

        DiscordGuild gld = channel.Guild;
        if (this.ActiveConnections.ContainsKey(gld.Id))
        {
            throw new InvalidOperationException("This guild already has a voice connection");
        }

        TaskCompletionSource<VoiceStateUpdateEventArgs> vstut = new TaskCompletionSource<VoiceStateUpdateEventArgs>();
        TaskCompletionSource<VoiceServerUpdateEventArgs> vsrut = new TaskCompletionSource<VoiceServerUpdateEventArgs>();
        this.VoiceStateUpdates[gld.Id] = vstut;
        this.VoiceServerUpdates[gld.Id] = vsrut;

        VoiceDispatch vsd = new VoiceDispatch
        {
            OpCode = 4,
            Payload = new VoiceStateUpdatePayload
            {
                GuildId = gld.Id,
                ChannelId = channel.Id,
                Deafened = false,
                Muted = false
            }
        };
        string vsj = JsonConvert.SerializeObject(vsd, Formatting.None);
        await (channel.Discord as DiscordClient).SendRawPayloadAsync(vsj);

        VoiceStateUpdateEventArgs vstu = await vstut.Task;
        VoiceStateUpdatePayload vstup = new VoiceStateUpdatePayload
        {
            SessionId = vstu.SessionId,
            UserId = vstu.User.Id
        };
        VoiceServerUpdateEventArgs vsru = await vsrut.Task;
        VoiceServerUpdatePayload vsrup = new VoiceServerUpdatePayload
        {
            Endpoint = vsru.Endpoint,
            GuildId = vsru.Guild.Id,
            Token = vsru.VoiceToken
        };

        VoiceNextConnection vnc = new VoiceNextConnection(this.Client, gld, channel, this.Configuration, vsrup, vstup);
        vnc.VoiceDisconnected += this.Vnc_VoiceDisconnected;
        await vnc.ConnectAsync();
        await vnc.WaitForReadyAsync();
        this.ActiveConnections[gld.Id] = vnc;
        return vnc;
    }

    /// <summary>
    /// Gets a VoiceNext connection for specified guild.
    /// </summary>
    /// <param name="guild">Guild to get VoiceNext connection for.</param>
    /// <returns>VoiceNext connection for the specified guild.</returns>
    public VoiceNextConnection GetConnection(DiscordGuild guild)
        => this.ActiveConnections.ContainsKey(guild.Id) ? this.ActiveConnections[guild.Id] : null;

    private async Task Vnc_VoiceDisconnected(DiscordGuild guild)
    {
        VoiceNextConnection vnc = null;
        if (this.ActiveConnections.ContainsKey(guild.Id))
        {
            this.ActiveConnections.TryRemove(guild.Id, out vnc);
        }

        VoiceDispatch vsd = new VoiceDispatch
        {
            OpCode = 4,
            Payload = new VoiceStateUpdatePayload
            {
                GuildId = guild.Id,
                ChannelId = null
            }
        };
        string vsj = JsonConvert.SerializeObject(vsd, Formatting.None);
        await (guild.Discord as DiscordClient).SendRawPayloadAsync(vsj);
    }

    private Task Client_VoiceStateUpdate(DiscordClient client, VoiceStateUpdateEventArgs e)
    {
        DiscordGuild gld = e.Guild;
        if (gld == null)
        {
            return Task.CompletedTask;
        }

        if (e.User == null)
        {
            return Task.CompletedTask;
        }

        if (e.User.Id == this.Client.CurrentUser.Id)
        {
            if (e.After.Channel == null && this.ActiveConnections.TryRemove(gld.Id, out VoiceNextConnection? ac))
            {
                ac.Disconnect();
            }

            if (this.ActiveConnections.TryGetValue(e.Guild.Id, out VoiceNextConnection? vnc))
            {
                vnc.TargetChannel = e.Channel;
            }

            if (!string.IsNullOrWhiteSpace(e.SessionId) && e.Channel != null && this.VoiceStateUpdates.TryRemove(gld.Id, out TaskCompletionSource<VoiceStateUpdateEventArgs>? xe))
            {
                xe.SetResult(e);
            }
        }

        return Task.CompletedTask;
    }

    private async Task Client_VoiceServerUpdate(DiscordClient client, VoiceServerUpdateEventArgs e)
    {
        DiscordGuild gld = e.Guild;
        if (gld == null)
        {
            return;
        }

        if (this.ActiveConnections.TryGetValue(e.Guild.Id, out VoiceNextConnection? vnc))
        {
            vnc.ServerData = new VoiceServerUpdatePayload
            {
                Endpoint = e.Endpoint,
                GuildId = e.Guild.Id,
                Token = e.VoiceToken
            };

            string eps = e.Endpoint;
            int epi = eps.LastIndexOf(':');
            string eph = string.Empty;
            int epp = 443;
            if (epi != -1)
            {
                eph = eps.Substring(0, epi);
                epp = int.Parse(eps.Substring(epi + 1));
            }
            else
            {
                eph = eps;
            }
            vnc.WebSocketEndpoint = new ConnectionEndpoint { Hostname = eph, Port = epp };

            vnc.Resume = false;
            await vnc.ReconnectAsync();
        }

        if (this.VoiceServerUpdates.ContainsKey(gld.Id))
        {
            this.VoiceServerUpdates.TryRemove(gld.Id, out TaskCompletionSource<VoiceServerUpdateEventArgs>? xe);
            xe.SetResult(e);
        }
    }

    public override void Dispose()
    {
        foreach (System.Collections.Generic.KeyValuePair<ulong, VoiceNextConnection> conn in this.ActiveConnections)
        {
            conn.Value?.Dispose();
        }

        if (this.Client != null)
        {
            this.Client.VoiceStateUpdated -= this.Client_VoiceStateUpdate;
            this.Client.VoiceServerUpdated -= this.Client_VoiceServerUpdate;
        }
        // Lo and behold, the audacious man who dared lay his hand upon VoiceNext hath once more trespassed upon its profane ground!

        // Satisfy rule CA1816. Can be removed if this class is sealed.
        GC.SuppressFinalize(this);
    }
}
