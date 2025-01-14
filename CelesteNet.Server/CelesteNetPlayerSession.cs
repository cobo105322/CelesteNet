﻿using Celeste.Mod.CelesteNet.DataTypes;
using Mono.Options;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod.CelesteNet.Server {
    public class CelesteNetPlayerSession : IDisposable {

        public static readonly HashSet<char> IllegalNameChars = new() { ':', '#', '|' };

        public readonly CelesteNetServer Server;
        public readonly CelesteNetConnection Con;
        public readonly uint ID;
        public readonly string ConUID;
        public string UID;

        private readonly RWLock StateLock = new();
        private readonly Dictionary<object, Dictionary<Type, object>> StateContexts = new();

        public DataPlayerInfo? PlayerInfo => Server.Data.TryGetRef(ID, out DataPlayerInfo? value) ? value : null;

        public Channel Channel;

        public DataNetEmoji? AvatarEmoji;

        private readonly object RequestNextIDLock = new();
        private uint RequestNextID = 0;

        public CelesteNetPlayerSession(CelesteNetServer server, CelesteNetConnection con, uint id) {
            Server = server;
            Con = con;
            ID = id;

            ConUID = UID = $"con-{con.UID}";

            Channel = server.Channels.Default;

            Con.OnSendFilter += ConSendFilter;
            Server.Data.RegisterHandlersIn(this);
        }

        public T? Get<T>(object ctx) where T : class {
            using (StateLock.R()) {
                if (!StateContexts.TryGetValue(ctx, out Dictionary<Type, object>? states))
                    return null;

                if (!states.TryGetValue(typeof(T), out object? state))
                    return null;

                return (T) state;
            }
        }

        [return: NotNullIfNotNull("state")]
        public T? Set<T>(object ctx, T? state) where T : class {
            if (state == null)
                return Remove<T>(ctx);

            using (StateLock.W()) {
                if (!StateContexts.TryGetValue(ctx, out Dictionary<Type, object>? states))
                    StateContexts[ctx] = states = new();

                states[typeof(T)] = state;
                return state;
            }
        }

        public T? Remove<T>(object ctx) where T : class {
            using (StateLock.W()) {
                if (!StateContexts.TryGetValue(ctx, out Dictionary<Type, object>? states))
                    return null;

                if (!states.TryGetValue(typeof(T), out object? state))
                    return null;

                states.Remove(typeof(T));
                if (states.Count == 0)
                    StateContexts.Remove(ctx);
                return (T) state;
            }
        }

        public void Start<T>(DataHandshakeClient<T> handshake) where T : DataHandshakeClient<T> {
            Logger.Log(LogLevel.INF, "playersession", $"Startup #{ID} {Con}");
            using (Server.ConLock.W())
                Server.Sessions.Add(this);
            Server.PlayersByCon[Con] = this;
            Server.PlayersByID[ID] = this;

            if (Server.UserData.TryLoad(UID, out BanInfo ban) && !ban.Reason.IsNullOrEmpty()) {
                Con.Send(new DataDisconnectReason { Text = string.Format(Server.Settings.MessageIPBan, ban.Reason) });
                Con.Send(new DataInternalDisconnect());
                return;
            }

            string name = handshake.Name;
            if (name.StartsWith("#")) {
                string uid = Server.UserData.GetUID(name.Substring(1));
                if (uid.IsNullOrEmpty()) {
                    Con.Send(new DataDisconnectReason { Text = Server.Settings.MessageInvalidUserKey });
                    Con.Send(new DataInternalDisconnect());
                    return;
                }
                UID = uid;

                if (!Server.UserData.TryLoad(uid, out BasicUserInfo userinfo)) {
                    Con.Send(new DataDisconnectReason { Text = Server.Settings.MessageUserInfoMissing });
                    Con.Send(new DataInternalDisconnect());
                    return;
                }

                name = userinfo.Name.Sanitize(IllegalNameChars, true);
                if (name.Length > Server.Settings.MaxNameLength)
                    name = name.Substring(0, Server.Settings.MaxNameLength);
                if (name.IsNullOrEmpty())
                    name = "Ghost";

                if (Server.UserData.TryLoad(UID, out ban) && !ban.Reason.IsNullOrEmpty()) {
                    Con.Send(new DataDisconnectReason { Text = string.Format(Server.Settings.MessageBan, name, ban.Reason) });
                    Con.Send(new DataInternalDisconnect());
                    return;
                }

            } else {
                if (Server.Settings.AuthOnly) {
                    Con.Send(new DataDisconnectReason { Text = Server.Settings.MessageAuthOnly });
                    Con.Send(new DataInternalDisconnect());
                    return;
                }

                name = name.Sanitize(IllegalNameChars);
                if (name.Length > Server.Settings.MaxGuestNameLength)
                    name = name.Substring(0, Server.Settings.MaxGuestNameLength);
                if (name.IsNullOrEmpty())
                    name = "Guest";
            }

            if (name.Length > Server.Settings.MaxNameLength)
                name = name.Substring(0, Server.Settings.MaxNameLength);

            string nameSpace = name;
            name = name.Replace(" ", "");
            string fullNameSpace = nameSpace;
            string fullName = name;

            using (Server.ConLock.R()) {
                int i = 1;
                while (true) {
                    bool conflict = false;
                    foreach (CelesteNetPlayerSession other in Server.Sessions)
                        if (conflict = other.PlayerInfo?.FullName == fullName)
                            break;
                    if (!conflict)
                        break;
                    i++;
                    fullNameSpace = $"{nameSpace}#{i}";
                    fullName = $"{name}#{i}";
                }
            }

            string displayName = fullNameSpace;

            using (Stream? avatar = Server.UserData.ReadFile(UID, "avatar.png")) {
                if (avatar != null) {
                    AvatarEmoji = new() {
                        ID = $"celestenet_avatar_{ID}_",
                        Data = avatar.ToBytes()
                    };
                    displayName = $":{AvatarEmoji.ID}: {fullNameSpace}";
                }
            }

            DataPlayerInfo playerInfo = new() {
                ID = ID,
                Name = name,
                FullName = fullName,
                DisplayName = displayName
            };
            playerInfo.Meta = playerInfo.GenerateMeta(Server.Data);
            Server.Data.SetRef(playerInfo);

            Logger.Log(LogLevel.INF, "playersession", playerInfo.ToString());

            Con.Send(new DataHandshakeServer {
                PlayerInfo = playerInfo
            });
            Con.Send(AvatarEmoji);

            DataInternalBlob? blobPlayerInfo = DataInternalBlob.For(Server.Data, playerInfo);
            DataInternalBlob? blobAvatarEmoji = DataInternalBlob.For(Server.Data, AvatarEmoji);

            using (Server.ConLock.R())
                foreach (CelesteNetPlayerSession other in Server.Sessions) {
                    if (other == this)
                        continue;

                    DataPlayerInfo? otherInfo = other.PlayerInfo;
                    if (otherInfo == null)
                        continue;

                    other.Con.Send(blobPlayerInfo);
                    other.Con.Send(blobAvatarEmoji);

                    Con.Send(otherInfo);
                    Con.Send(other.AvatarEmoji);

                    foreach (DataType bound in Server.Data.GetBoundRefs(otherInfo))
                        if (!bound.Is<MetaPlayerPrivateState>(Server.Data) || other.Channel.ID == 0)
                            Con.Send(bound);
                }

            ResendPlayerStates();

            Server.InvokeOnSessionStart(this);
        }

        public Action WaitFor<T>(DataFilter<T> cb) where T : DataType<T>
            => WaitFor(0, cb, null);

        public Action WaitFor<T>(int timeout, DataFilter<T> cb, Action? cbTimeout = null) where T : DataType<T>
            => Server.Data.WaitFor<T>(timeout, (con, data) => {
                if (Con != con)
                    return false;
                return cb(con, data);
            }, cbTimeout);

        public Action Request<T>(DataHandler<T> cb) where T : DataType<T>, IDataRequestable
            => Request(0, Activator.CreateInstance(typeof(T).GetRequestType()) as DataType ?? throw new Exception($"Invalid requested type: {typeof(T).FullName}"), cb, null);

        public Action Request<T>(int timeout, DataHandler<T> cb, Action? cbTimeout = null) where T : DataType<T>, IDataRequestable
            => Request(timeout, Activator.CreateInstance(typeof(T).GetRequestType()) as DataType ?? throw new Exception($"Invalid requested type: {typeof(T).FullName}"), cb, cbTimeout);

        public Action Request<T>(DataType req, DataHandler<T> cb) where T : DataType<T>, IDataRequestable
            => Request(0, req, cb, null);

        public Action Request<T>(int timeout, DataType req, DataHandler<T> cb, Action? cbTimeout = null) where T : DataType<T>, IDataRequestable {
            using (req.UpdateMeta(Server.Data)) {
                if (!req.TryGet(Server.Data, out MetaRequest? mreq))
                    mreq = new();
                lock (RequestNextIDLock)
                    mreq.ID = RequestNextID++;
                req.Set(Server.Data, mreq);
            }

            Action cancel = WaitFor<T>(timeout, (con, data) => {
                if (req.TryGet(Server.Data, out MetaRequest? mreq) &&
                    data.TryGet(Server.Data, out MetaRequestResponse? mres) &&
                    mreq.ID != mres.ID)
                    return false;

                cb(con, data);
                return true;
            }, cbTimeout);

            Con.Send(req);
            return cancel;
        }

        public void ResendPlayerStates() {
            Channel channel = Channel;

            ILookup<bool, DataInternalBlob> boundAll = Server.Data.GetBoundRefs(PlayerInfo)
                .Select(bound => new DataInternalBlob(Server.Data, bound))
                .ToLookup(blob => blob.Data.Is<MetaPlayerPrivateState>(Server.Data));
            IEnumerable<DataInternalBlob> boundAllPublic = boundAll[false];
            IEnumerable<DataInternalBlob> boundAllPrivate = boundAll[true];

            using (Server.ConLock.R())
                foreach (CelesteNetPlayerSession other in Server.Sessions) {
                    if (other == this)
                        continue;

                    foreach (DataType bound in boundAllPublic)
                        other.Con.Send(bound);
                    foreach (DataType bound in boundAllPrivate)
                        if (channel == other.Channel)
                            other.Con.Send(bound);

                    DataPlayerInfo? otherInfo = other.PlayerInfo;
                    if (otherInfo == null)
                        continue;

                    foreach (DataType bound in Server.Data.GetBoundRefs(otherInfo))
                        if (!bound.Is<MetaPlayerPrivateState>(Server.Data) || channel == other.Channel)
                            Con.Send(bound);
                }
        }

        public bool IsSameArea(CelesteNetPlayerSession other)
            => Server.Data.TryGetBoundRef(PlayerInfo, out DataPlayerState? state) && state != null && IsSameArea(Channel, state, other);

        public bool IsSameArea(Channel channel, DataPlayerState? state, CelesteNetPlayerSession other)
            =>  state != null &&
                other.Channel == channel &&
                Server.Data.TryGetBoundRef(other.PlayerInfo, out DataPlayerState? otherState) &&
                otherState != null &&
                otherState.SID == state.SID &&
                otherState.Mode == state.Mode;

        public bool ConSendFilter(CelesteNetConnection con, DataType data) {
            if (Server.Data.TryGetBoundRef(PlayerInfo, out DataNetFilterList? list) && list != null) {
                string source = data.GetSource(Server.Data);
                return string.IsNullOrEmpty(source) || list.Contains(source);
            }

            return true;
        }

        public event Action<CelesteNetPlayerSession, DataPlayerInfo?>? OnEnd;

        public void Dispose() {
            Logger.Log(LogLevel.INF, "playersession", $"Shutdown #{ID} {Con}");

            DataPlayerInfo? playerInfoLast = PlayerInfo;

            using (Server.ConLock.W())
                Server.Sessions.Remove(this);
            Server.PlayersByCon.TryRemove(Con, out _);
            Server.PlayersByID.TryRemove(ID, out _);

            if (playerInfoLast != null)
                Server.BroadcastAsync(new DataPlayerInfo {
                    ID = ID
                });

            Con.OnSendFilter -= ConSendFilter;
            Server.Data.UnregisterHandlersIn(this);

            Logger.Log(LogLevel.VVV, "playersession", $"Loopend send #{ID} {Con}");
            Con.Send(new DataInternalLoopend(() => {
                Logger.Log(LogLevel.VVV, "playersession", $"Loopend run #{ID} {Con}");

                Server.Data.FreeRef<DataPlayerInfo>(ID);
                Server.Data.FreeOrder<DataPlayerFrame>(ID);

                OnEnd?.Invoke(this, playerInfoLast);
            }));

            StateLock.Dispose();
        }


        #region Handlers

        public bool Filter(CelesteNetConnection con, DataPlayerInfo updated) {
            // Make sure that a player can only update their own info.
            if (con != Con)
                return true;

            DataPlayerInfo? old = PlayerInfo;
            if (old == null)
                return true;

            updated.ID = old.ID;
            updated.Name = old.Name;
            updated.FullName = old.FullName;
            updated.DisplayName = old.DisplayName;

            return true;
        }

        public bool Filter(CelesteNetConnection con, DataType data) {
            if (con != Con)
                return true;

            bool fixup = false;
            DataPlayerInfo? player = null;

            if (data.TryGet(Server.Data, out MetaPlayerUpdate? update)) {
                update.Player = player ??= PlayerInfo;
                fixup = true;
            }

            if (data.TryGet(Server.Data, out MetaPlayerPrivateState? state)) {
                state.Player = player ??= PlayerInfo;
                fixup = true;
            }

            if (data.TryGet(Server.Data, out MetaPlayerPublicState? statePub)) {
                statePub.Player = player ??= PlayerInfo;
                fixup = true;
            }

            if (data.TryGet(Server.Data, out MetaBoundRef? boundRef) && boundRef.TypeBoundTo == DataPlayerInfo.DataID) {
                boundRef.ID = (player ?? PlayerInfo)?.ID ?? uint.MaxValue;
                fixup = true;
            }

            if (fixup)
                data.FixupMeta(Server.Data);

            return true;
        }

        public bool Filter(CelesteNetConnection con, DataPlayerFrame frame) {
            if (frame.HairCount > Server.Settings.MaxHairLength)
                frame.HairCount = Server.Settings.MaxHairLength;

            if (frame.Followers.Length > Server.Settings.MaxFollowers)
                Array.Resize(ref frame.Followers, Server.Settings.MaxFollowers);

            return true;
        }

        public void Handle(CelesteNetConnection con, DataPlayerInfo updated) {
            if (con != Con)
                return;

            DataInternalBlob blob = new(Server.Data, updated);

            using (Server.ConLock.R())
                foreach (CelesteNetPlayerSession other in Server.Sessions) {
                    if (other == this)
                        continue;

                    other.Con.Send(blob);
                }
        }

        public void Handle(CelesteNetConnection con, DataType data) {
            if (con != Con)
                return;

            if (!Server.Data.TryGetBoundRef(PlayerInfo, out DataPlayerState? state))
                state = null;

            bool isPrivate = data.Is<MetaPlayerPrivateState>(Server.Data);
            bool isUpdate = data.Is<MetaPlayerUpdate>(Server.Data);
            if (data.Is<MetaPlayerPublicState>(Server.Data) ||
                isPrivate ||
                isUpdate) {
                Channel channel = Channel;

                DataInternalBlob blob = new(Server.Data, data);

                HashSet<CelesteNetPlayerSession> others = isPrivate || isUpdate ? channel.Players : Server.Sessions;
                using (isPrivate || isUpdate ? channel.Lock.R() :  Server.ConLock.R())
                    foreach (CelesteNetPlayerSession other in others) {
                        if (other == this)
                            continue;

                        /*
                        if (data.Is<MetaPlayerPrivateState>(Server.Data) && channel != other.Channel)
                            continue;
                        */

                        if (isUpdate && !IsSameArea(channel, state, other))
                            continue;

                        other.Con.Send(blob);
                    }
            }
        }

        #endregion

    }
}
