﻿using Celeste.Mod.CelesteNet.DataTypes;
using Microsoft.Xna.Framework;
using Monocle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.ObjectFactories;

namespace Celeste.Mod.CelesteNet {
    public class CelesteNetBinaryReader : BinaryReader {

        public readonly DataContext Data;

        public StringMap? Strings;

        public CelesteNetBinaryReader(DataContext ctx, StringMap? strings, Stream input)
            : base(input, CelesteNetUtils.UTF8NoBOM) {
            Data = ctx;
            Strings = strings;
        }

        public CelesteNetBinaryReader(DataContext ctx, StringMap? strings, Stream input, bool leaveOpen)
            : base(input, CelesteNetUtils.UTF8NoBOM, leaveOpen) {
            Data = ctx;
            Strings = strings;
        }

        public virtual Vector2 ReadVector2()
            => new(ReadSingle(), ReadSingle());
        public virtual Vector2 ReadVector2Scale()
            => new(Calc.Clamp(ReadSingle(), -3f, 3f), Calc.Clamp(ReadSingle(), -3f, 3f));

        public virtual Color ReadColor()
            => new(ReadByte(), ReadByte(), ReadByte(), ReadByte());

        public virtual Color ReadColorNoA()
            => new(ReadByte(), ReadByte(), ReadByte(), 255);

        public virtual DateTime ReadDateTime()
            => DateTime.FromBinary(ReadInt64());

        [ThreadStatic]
        private static char[]? charsShared;
        private unsafe void ReadNetStringCore(char c, out char[] chars, out int i) {
            const int buffer = 8;
            chars = charsShared ?? new char[128];
            int length = chars.Length;
            i = 0;
            goto Read;

            Resize:
            length *= 2;
            Array.Resize(ref chars, length);

            Read:
            fixed (char* ptr = chars) {
                ptr[i++] = c;
                while ((c = ReadChar()) != '\0') {
                    if (i > 4096)
                        throw new Exception("String too long.");
                    if ((i + buffer) >= length)
                        goto Resize;
                    ptr[i++] = c;
                }
                for (int j = 0; j < buffer; j++)
                    ptr[i++] = '\0';
                i -= buffer;
            }

            charsShared = chars;
        }

        public virtual string ReadNetString() {
            char c = (char) ReadByte();
            if (c == 0xFF)
                throw new Exception("Trying to read a mapped string as a non-mapped string!");

            if (c == 0x00)
                return "";

            ReadNetStringCore(c, out char[] chars, out int i);
            return chars.ToDedupedString(i);
        }

        public virtual string ReadNetMappedString() {
            string value;

            char c = (char) ReadByte();
            if (c == 0xFF) {
                if (Strings == null)
                    throw new Exception("Trying to read a mapped string without a string map!");
                value = Strings.Get(Read7BitEncodedInt());
                return value;
            }

            if (c == 0x00)
                return "";

            ReadNetStringCore(c, out char[] chars, out int i);
            value = chars.ToDedupedString(i);

            Strings?.CountRead(value);
            return value;
        }

        public T? ReadRef<T>() where T : DataType<T>
            => Data.GetRef<T>(ReadUInt32());

        public T? ReadOptRef<T>() where T : DataType<T>
            => Data.TryGetRef(ReadUInt32(), out T? value) ? value : null;

    }
}
