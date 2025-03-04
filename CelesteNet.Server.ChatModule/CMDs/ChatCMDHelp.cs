﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Celeste.Mod.CelesteNet.Server.Chat {
    public class ChatCMDHelp : ChatCMD {

        public override string Args => "[page] | [command]";

        public override string Info => "Get help on how to use commands.";

        public override int HelpOrder => int.MinValue;

        public override void Run(ChatCMDEnv env, List<ChatCMDArg> args) {
            if (args.Count == 1) {
                if (args[0].Type == ChatCMDArgType.Int) {
                    env.Send(GetCommandPage(env, args[0].Int - 1));
                    return;
                }

                env.Send(GetCommandSnippet(env, args[0].String));
                return;
            }

            env.Send(GetCommandPage(env, 0));
        }

        public string GetCommandPage(ChatCMDEnv env, int page = 0) {
            const int pageSize = 8;

            string prefix = Chat.Settings.CommandPrefix;
            StringBuilder builder = new();

            int pages = (int) Math.Ceiling(Chat.Commands.All.Count / (float) pageSize);
            if (page < 0 || pages <= page)
                throw new Exception("Page out of range.");

            for (int i = page * pageSize; i < (page + 1) * pageSize && i < Chat.Commands.All.Count; i++) {
                ChatCMD cmd = Chat.Commands.All[i];
                builder
                    .Append(prefix)
                    .Append(cmd.ID)
                    .Append(" ")
                    .Append(cmd.Args)
                    .AppendLine();
            }

            builder
                .Append("Page ")
                .Append(page + 1)
                .Append("/")
                .Append(pages);

            return builder.ToString().Trim();
        }

        public string GetCommandSnippet(ChatCMDEnv env, string cmdName) {
            ChatCMD? cmd = Chat.Commands.Get(cmdName);
            if (cmd == null)
                throw new Exception($"Command {cmdName} not found.");

            return Help_GetCommandSnippet(env, cmd);
        }

        public string Help_GetCommandSnippet(ChatCMDEnv env, ChatCMD cmd) {
            string prefix = Chat.Settings.CommandPrefix;
            StringBuilder builder = new();

            builder
                .Append(prefix)
                .Append(cmd.ID)
                .Append(" ")
                .Append(cmd.Args)
                .AppendLine()
                .AppendLine(cmd.Help);

            return builder.ToString().Trim();
        }

    }
}
