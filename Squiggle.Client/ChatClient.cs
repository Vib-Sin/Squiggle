﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Squiggle.Core;
using Squiggle.Core.Chat;
using Squiggle.Core.Presence;
using Squiggle.History;
using Squiggle.Utilities;

namespace Squiggle.Client
{
    public class ChatClient: IChatClient
    {
        IChatService chatService;
        IPresenceService presenceService;
        SquiggleEndPoint chatEndPoint;
        BuddyList buddies;

        public event EventHandler<ChatStartedEventArgs> ChatStarted = delegate { };
        public event EventHandler<BuddyOnlineEventArgs> BuddyOnline = delegate { };
        public event EventHandler<BuddyEventArgs> BuddyOffline = delegate { };
        public event EventHandler<BuddyEventArgs> BuddyUpdated = delegate { };

        public ISelfBuddy CurrentUser { get; private set; }

        public IEnumerable<IBuddy> Buddies 
        {
            get { return buddies; }
        }

        public bool LoggedIn { get; private set; }

        public bool EnableLogging { get; set; }

        public ChatClient(ChatClientOptions options)
        {
            chatService = new ChatService(options.ChatEndPoint);
            buddies = new BuddyList();
            chatService.ChatStarted += new EventHandler<Squiggle.Core.Chat.ChatStartedEventArgs>(chatService_ChatStarted);

            var presenceOptions = new PresenceServiceOptions()
            {
                ChatEndPoint = options.ChatEndPoint,
                MulticastEndPoint = options.MulticastEndPoint,
                MulticastReceiveEndPoint = options.MulticastReceiveEndPoint,
                PresenceServiceEndPoint = options.PresenceServiceEndPoint,
                KeepAliveTime = options.KeepAliveTime
            };
            presenceService = new PresenceService(presenceOptions);
            presenceService.UserOffline += new EventHandler<UserEventArgs>(presenceService_UserOffline);
            presenceService.UserOnline += new EventHandler<UserOnlineEventArgs>(presenceService_UserOnline);
            presenceService.UserUpdated += new EventHandler<UserEventArgs>(presenceService_UserUpdated);
            this.chatEndPoint = options.ChatEndPoint;
        }        

        public IChat StartChat(IBuddy buddy)
        {
            IChatSession session = chatService.CreateSession(new SquiggleEndPoint(buddy.Id, ((Buddy)buddy).ChatEndPoint));
            var chat = new Chat(session, CurrentUser, buddy, id=>buddies[id]);
            return chat;
        }        

        public void Login(string username, IBuddyProperties properties)
        {
            username = username.Trim();

            chatService.Start();
            presenceService.Login(username, properties);

            var self = new SelfBuddy(this, chatEndPoint.ClientID, username, UserStatus.Online, properties); 
            self.EnableUpdates = true;
            CurrentUser = self;
            LogStatus(self);
            LoggedIn = true;
        }        

        public void Logout()
        {
            LoggedIn = false;
            buddies.Clear();
            chatService.Stop();
            presenceService.Logout();

            var self = (SelfBuddy)CurrentUser;
            self.EnableUpdates = false;
            self.Status = UserStatus.Offline;

            LogStatus(CurrentUser);
        }
        
        void Update()
        {
            LogStatus(CurrentUser);
            var properties = CurrentUser.Properties.Clone();
            presenceService.Update(CurrentUser.DisplayName, properties, CurrentUser.Status);
        }

        void chatService_ChatStarted(object sender, Squiggle.Core.Chat.ChatStartedEventArgs e)
        {
            var buddyList = new List<IBuddy>();
            foreach (SquiggleEndPoint user in e.Session.RemoteUsers)
            {
                Buddy buddy = buddies[user.ClientID];
                if (buddy != null)
                    buddyList.Add(buddy);
            }
            if (buddyList.Count > 0)
            {
                var chat = new Chat(e.Session, CurrentUser, buddyList, id=>buddies[id]);
                ChatStarted(this, new ChatStartedEventArgs() { Chat = chat, Buddies = buddyList });
            }
        }

        void presenceService_UserUpdated(object sender, UserEventArgs e)
        {
            var buddy = buddies[e.User.ID];
            if (buddy != null)
            {
                UserStatus lastStatus = buddy.Status;
                UpdateBuddy(buddy, e.User);

                if (lastStatus != UserStatus.Offline && !buddy.IsOnline)
                    OnBuddyOffline(buddy);
                else if (lastStatus == UserStatus.Offline && buddy.IsOnline)
                    OnBuddyOnline(buddy, false);
                else
                    OnBuddyUpdated(buddy);
            }
        }        

        void presenceService_UserOnline(object sender, UserOnlineEventArgs e)
        {
            var buddy = buddies[e.User.ID];
            if (buddy == null)
            {
                buddy = new Buddy(e.User.ID, e.User.DisplayName, e.User.Status, e.User.ChatEndPoint, new BuddyProperties(e.User.Properties));
                buddies.Add(buddy);
            }
            else
                UpdateBuddy(buddy, e.User);
            
            OnBuddyOnline(buddy, e.Discovered);
        }        

        void presenceService_UserOffline(object sender, UserEventArgs e)
        {
            var buddy = buddies[e.User.ID];
            if (buddy != null)
            {
                buddy.Update(e.User.Status, e.User.DisplayName, e.User.ChatEndPoint, e.User.Properties);
                OnBuddyOffline(buddy);
            }
        }

        void OnBuddyUpdated(Buddy buddy)
        {
            LogStatus(buddy);
            BuddyUpdated(this, new BuddyEventArgs( buddy ));
        } 

        void OnBuddyOnline(IBuddy buddy, bool discovered)
        {
            if (!discovered)
                LogStatus(buddy);
            BuddyOnline(this, new BuddyOnlineEventArgs() { Buddy = buddy, Discovered = discovered });
        }

        void OnBuddyOffline(IBuddy buddy)
        {
            LogStatus(buddy);
            BuddyOffline(this, new BuddyEventArgs( buddy ));
        }

        void UpdateBuddy(IBuddy buddy, IUserInfo user)
        {
            ((Buddy)buddy).Update(user.Status, user.DisplayName, user.ChatEndPoint, user.Properties);
        }

        void LogStatus(IBuddy buddy)
        {
            if (EnableLogging)
                ExceptionMonster.EatTheException(() =>
                {
                    var manager = new HistoryManager();
                    manager.AddStatusUpdate(DateTime.Now, new Guid(buddy.Id), buddy.DisplayName, (int)buddy.Status);
                }, "logging history.");
        }

        #region IDisposable Members

        public void Dispose()
        {
            Logout();
        }

        #endregion

        class SelfBuddy : Buddy, ISelfBuddy
        {
            IChatClient client;

            public bool EnableUpdates { get; set; }

            public SelfBuddy(IChatClient client, string id, string displayName, UserStatus status, IBuddyProperties properties) : base(id, displayName, status, null, properties)
            {
                this.client = client;
            }

            public new string DisplayName
            {
                get { return base.DisplayName; }
                set
                {
                    base.DisplayName = value;
                    Update();
                }
            }
            
            public new UserStatus Status
            {
                get { return base.Status; }
                set
                {
                    base.Status = value;
                    Update();
                }
            }

            protected override void OnBuddyPropertiesChanged()
            {
                base.OnBuddyPropertiesChanged();
                Update();
            }

            void Update()
            {
                if (EnableUpdates)
                    ((ChatClient)client).Update();
            }
        }
    }
}
