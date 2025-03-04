﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoMod.Utils;

namespace Celeste.Mod.CelesteNet.DataTypes {
    public abstract class MetaType {

        public abstract void Read(DataContext ctx, MetaTypeWrap data);
        public abstract void Write(DataContext ctx, MetaTypeWrap data);

    }

    public abstract class MetaType<T> : MetaType where T : MetaType<T> {

        public static string MetaID;

        static MetaType() {
            MetaID = typeof(T).Name;
        }

        public T ReadT(DataContext ctx, MetaTypeWrap data) {
            Read(ctx, data);
            return (T) this;
        }

    }

    public class MetaTypeWrap {

        public string ID = "";
        public Dictionary<string, string> Data = new();

        public string this[string key] {
            get => Data[key];
            set => Data[key] = value;
        }

        public MetaType Unwrap(DataContext ctx) {
            MetaType meta = Activator.CreateInstance(ctx.IDToMetaType[ID]) as MetaType ?? throw new Exception($"Couldn't unwrap MetaType {ID}");
            meta.Read(ctx, this);
            return meta;
        }

        public MetaTypeWrap Wrap(DataContext ctx, MetaType meta) {
            ID = ctx.MetaTypeToID[meta.GetType()];
            meta.Write(ctx, this);
            return this;
        }

        public MetaTypeWrap Read(CelesteNetBinaryReader reader) {
            ID = reader.ReadNetMappedString();
            int count = reader.ReadByte();
            for (int i = 0; i < count; i++)
                Data[reader.ReadNetMappedString()] = reader.ReadNetMappedString();
            return this;
        }

        public void Write(CelesteNetBinaryWriter writer) {
            writer.WriteNetMappedString(ID);
            writer.Write((byte) Data.Count);
            foreach (KeyValuePair<string, string> kvp in Data) {
                writer.WriteNetMappedString(kvp.Key);
                writer.WriteNetMappedString(kvp.Value);
            }
        }

    }
}
