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
        public abstract Stream? ReadFile(string uid, string name);
        public abstract void Save<T>(string uid, T value) where T : notnull;
        public abstract Stream WriteFile(string uid, string name);
        public abstract void Delete<T>(string uid);
        public abstract void DeleteFile(string uid, string name);
        public abstract void Wipe(string uid);

        public abstract T[] LoadRegistered<T>() where T : new();
        public abstract T[] LoadAll<T>() where T : new();

        public abstract string[] GetRegistered();
        public abstract string[] GetAll();

        public abstract int GetRegisteredCount();
        public abstract int GetAllCount();

        public abstract string Create(string uid);
        public abstract void RevokeKey(string key);

    }

    public class BasicUserInfo {
        public string Name { get; set; } = "";
        // TODO: Move into separate Discord module!
        public string Discrim { get; set; } = "";
        public HashSet<string> Tags { get; set; } = new HashSet<string>();
    }

    public class BanInfo {
        public string Reason { get; set; } = "";
        public DateTime? From { get; set; } = null;
        public DateTime? To { get; set; } = null;
    }
}
