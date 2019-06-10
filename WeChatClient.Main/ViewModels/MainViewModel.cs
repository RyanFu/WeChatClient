﻿using Newtonsoft.Json.Linq;
using Prism.Ioc;
using Prism.Modularity;
using Prism.Regions;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Unity.Attributes;
using WeChatClient.Core.Dependency;
using WeChatClient.Core.Helpers;
using WeChatClient.Core.Http;
using WeChatClient.Core.Interfaces;
using WeChatClient.Core.Models;

namespace WeChatClient.Main.ViewModels
{
    [ExposeServices(ServiceLifetime.Singleton,typeof(IMainManager))]
    public class MainViewModel : ReactiveObject, IMainManager
    {
        private WeChatService wcs = new WeChatService();

        private readonly IRegionManager _regionManager;

        [Dependency]
        protected IContactListManager ContactListManager { get; set; }
        [Dependency]  //属性注入，相比构造注入使用比较方便
        protected IChatListManager ChatListManager { get; set; }
        [Dependency]
        protected IImageDownloadService ImageDownloadService { get; set; }

        [Reactive]
        public WeChatUser WeChatUser { get; private set; }

        public ICommand LoadedCommand { get; }

        public ICommand NavigateCommand { get; set; }

        public MainViewModel(IRegionManager regionManager)
        {
            _regionManager = regionManager;
            LoadedCommand = ReactiveCommand.CreateFromTask(InitAsync);
            NavigateCommand = ReactiveCommand.Create<string>(Navigate);
        }

        private void Navigate(string navigatePath)
        {
            if (navigatePath != null)
                _regionManager.RequestNavigate(WeChatClientConst.NavRegionName, navigatePath);
        }

        /// <summary>
        /// 异步初始化数据
        /// </summary>
        /// <returns></returns>
        private async Task InitAsync()
        {
            var list = await Task.Run(() =>
            {
                JObject init_result = wcs.WeChatInit();
                WeChatUser = JObjectToUser(init_result["User"]);
                ImageDownloadService.Add(WeChatUser);
                return init_result["ContactList"].Select(contact=> JObjectToUser(contact));
            });
            //将数据传输到聊天列表组件
            ChatListManager.AddChat(list.Distinct(new WeChatUserComparer()).ToArray());

            //开启微信状态通知
            await Task.Run(() =>
            {
                wcs.WxStatusNotify(WeChatUser.UserName);
            });

            //加载通讯录
            await LoadAllContact();
            //加载群组成员
            await UpdateInitGroupMember(list.Where(p => p.IsRoomContact()).Select(p => p.UserName).Distinct().ToArray());

            StartWeChatSyncTask();
        }

        /// <summary>
        /// 加载所有通讯录
        /// </summary>
        /// <returns></returns>
        private async Task LoadAllContact()
        {
            var list = await Task.Run(() =>
            {
                //取到通讯录，过滤公众号，然后分组
                JObject contact_result = wcs.GetContact();
                return contact_result["MemberList"]
                .Select(contact => JObjectToUser(contact)).OrderBy(p => p.StartChar).ToArray();
            });

            //将数据传输到通讯录组件
            ContactListManager.AddContact(list);
        }

        /// <summary>
        /// 加载初始化的群组聊天成员（这里很奇怪，微信初始化接口返回的群组成员信息不全，没有昵称）
        /// </summary>
        /// <returns></returns>
        private async Task UpdateInitGroupMember(string[] userNames)
        {
            var list = await Task.Run(() =>
            {
                JObject contact_result = wcs.WxBatchGetContact(userNames);
                return contact_result["ContactList"].Select(contact => JObjectToUser(contact)).ToArray();
            });

            //将初始化的群组聊天成员传输到聊天列表组件
            ChatListManager.UpdateInitGroupMember(list);
        }

        private WeChatUser JObjectToUser(JToken jObject)
        {
            WeChatUser user = jObject.ToObject<WeChatUser>();

            user.HeadImgUrl = user.UserName.GetIconUrl();
            user.ChatNotifyClose = user.IsChatNotifyClose();
            user.StartChar = user.GetStartChar();
            //user.MemberList = jObject["MemberList"].Select(p => p.ToObject<ChatRoomMember>()).ToList();
            if (string.IsNullOrEmpty(user.NickName) && user.MemberList != null)
                user.NickName = string.Join(",", user.MemberList.Select(p => p.NickName));

            return user;
        }

        private WeChatMessage JObjectToMessage(JToken jObject)
        {
            WeChatMessage message = jObject.ToObject<WeChatMessage>();
            //message.Content = message.MsgType == 1 ? message.Content : "请在其他设备上查看消息";//只接受文本消息
            message.CreateDateTime = message.CreateTime.ToTime();
            message.GroupDateTime = message.CreateDateTime;
            message.GroupShortTime = message.CreateDateTime.ToString("HH:mm");
            message.IsReceive = message.ToUserName == WeChatUser.UserName;
            return message;
        }

        /// <summary>
        /// 开启微信同步任务
        /// </summary>
        private void StartWeChatSyncTask()
        {
            Task.Run(() =>
            {
                while (true)
                {
                    //同步检查
                    string sync_flag = wcs.WeChatSyncCheck();
                    if (sync_flag == null)
                    {
                        Thread.Sleep(100);
                        continue;
                    }
                    //这里应该判断sync_flag中Selector的值
                    else
                    {
                        JObject sync_result = wcs.WeChatSync();//进行同步
                        if (sync_result != null)
                        {
                            if (sync_result["ModContactCount"] != null && sync_result["ModContactCount"].ToString() != "0")
                            {
                                var addChatList = sync_result["ModContactList"].Select(p => JObjectToUser(p));
                                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    ChatListManager.AddChat(addChatList.ToArray());
                                }));
                            }
                            if (sync_result["AddMsgCount"] != null && sync_result["AddMsgCount"].ToString() != "0")
                            {
                                var messageList = sync_result["AddMsgList"].Select(p => JObjectToMessage(p));

                                var loadMore = messageList.FirstOrDefault(p => p.IsLoadMoreChats);
                                if (loadMore != null)
                                {
                                    //加载更多聊天列表
                                    string[] userNames = loadMore.StatusNotifyUserName.Split(',').Where(p => !ChatListManager.Contains(p)).ToArray();
                                    LoadMoreChats(userNames);
                                }
                                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    ChatListManager.SyncMessage(messageList.ToArray());
                                }));      
                            }
                        }
                    }
                    Thread.Sleep(100);
                }
            });
        }

        private void LoadMoreChats(string[] userNames)
        {
            //启动新线程加载更多聊天列表
            Task.Run(() =>
            {
                for (int i = 0; i < Math.Ceiling(userNames.Length / 50.0); i++)  //每次最多查询50条数据
                {
                    JObject contact_result = wcs.WxBatchGetContact(userNames.Skip(i * 50).Take(50).ToArray());
                    var chatList = contact_result["ContactList"].Select(contact => JObjectToUser(contact)).ToArray();
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ChatListManager.AddChat(chatList);
                    }));
                }
            });
        }
    }
}
