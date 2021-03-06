﻿using Prism.Events;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using WeChatClient.Core.Dependency;
using WeChatClient.Core.Events;
using WeChatClient.Core.Interfaces;
using WeChatClient.Core.Models;
using WeChatClient.EmojiCore.Emoji;

namespace WeChatClient.ChatContent.ViewModels
{
    [ExposeServices(ServiceLifetime.Singleton, typeof(IChatContentManager))]
    public class ChatContentViewModel : ReactiveObject, IChatContentManager
    {
        /// <summary>
        /// 当前选中聊天
        /// </summary>
        [Reactive]
        public WeChatUser SelectedChat { get; set; }
        /// <summary>
        /// 有聊天被选中
        /// </summary>
        public bool HasChatSelected { [ObservableAsProperty]get; }
        /// <summary>
        /// 文本框
        /// </summary>
        [Reactive]
        public FlowDocument Message { get; private set; }
        /// <summary>
        /// 发送文本消息命令
        /// </summary>
        public ICommand SendTextMsgCommand { get; private set; }

        public ChatContentViewModel(IEventAggregator ea, EmojiManager emojiManager)
        {
            Message = new FlowDocument();

            SendTextMsgCommand = ReactiveCommand.Create(() =>
            {
                string msg = emojiManager.FlowDocumentToString(Message);
                if (string.IsNullOrWhiteSpace(msg))
                    return;
                ea.GetEvent<SendTextMsgEvent>().Publish(msg);
                Message.Blocks.Clear();
            });

            var observable = this.WhenAnyValue(p => p.SelectedChat);
            observable.Select(p => p != null).ToPropertyEx(this, p => p.HasChatSelected);
        }
    }
}
