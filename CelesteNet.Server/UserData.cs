﻿using Celeste.Mod.CelesteNet.DataTypes;
using Mono.Options;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod.CelesteNet.Server {
    public abstract class UserData {

        public readonly CelesteNetServer Server;

        public UserData(CelesteNetServer server) {
            Server = server;
        }

        public abstract string GetUID(string key);
        public abstract string GetKey(string uid);

        public abstract bool TryLoad<T>(string uid, out T value) where T : new();
        public T Load<T>(string uid) where T : new()
            => TryLoad(uid, out T value) ? value : value;
        public abstract void Save<T>(string uid, T value) where T : notnull;

        public abstract void Delete<T>(string uid);
        public abstract void DeleteAll(string uid);

        public abstract string Create(string uid);

    }

    public class BasicUserInfo {
        public string Name { get; set; } = "";
        // TODO: Move into separate Discord module!
        public string Discrim { get; set; } = "";
        public string Avatar { get; set; } = "";
        public HashSet<string> Tags { get; set; } = new HashSet<string>();
    }

    public class BanInfo {
        public string Reason { get; set; } = "";
        public DateTime? From { get; set; } = null;
        public DateTime? To { get; set; } = null;
    }
}