using Comfort.Common;
using EFT;
using EFT.Communications;
using Fika.Core.Coop.Custom;
using Fika.Core.Coop.HostClasses;
using Fika.Core.Coop.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using LiteNetLib;
using RevivalMod.Components;
using RevivalMod.Features;
using RevivalMod.FikaModule.Packets;
using System;
using UnityEngine;

namespace RevivalMod.FikaModule.Common
{
    internal class FikaMethods
    {
        

        public static void SendPlayerPositionPacket(string playerId, DateTime timeOfDeath, Vector3 position)
        {
            PlayerPositionPacket packet = new PlayerPositionPacket
            {
                playerId = playerId,
                timeOfDeath = timeOfDeath,
                position = position
            };

            if (Singleton<FikaServer>.Instantiated)
            {

                try
                {
                    Singleton<FikaServer>.Instance.SendDataToAll(ref packet, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex)
                {
                   
                }
            }
            else if (Singleton<FikaClient>.Instantiated)
            {
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }
           
        }
        public static void SendRemovePlayerFromCriticalPlayersListPacket(string playerId)
        {
            RemovePlayerFromCriticalPlayersListPacket packet = new RemovePlayerFromCriticalPlayersListPacket()
            {
                playerId = playerId
            };

            if (Singleton<FikaServer>.Instantiated)
            {
              
                try
                {
                    Singleton<FikaServer>.Instance.SendDataToAll(ref packet, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex)
                {

                }
            }
            else if (Singleton<FikaClient>.Instantiated)
            {
              
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }
            
           
        }

        public static void SendReviveMePacket(string reviveeId, string reviverId)
        {
            ReviveMePacket packet = new ReviveMePacket()
            {
                reviverId = reviverId,
                reviveeId = reviveeId
            };

            if (Singleton<FikaServer>.Instantiated)
            {
               
                try
                {
                    Singleton<FikaServer>.Instance.SendDataToAll(ref packet, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex)
                {
                   
                }
            }
            else if (Singleton<FikaClient>.Instantiated)
            {
               
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }
            else
            {
               
            }

        }
        public static void SendRevivedPacket(string reviverId, NetPeer peer)
        {
            RevivedPacket packet = new RevivedPacket()
            {
                reviverId = reviverId
            };

            if (Singleton<FikaServer>.Instantiated)
            {
               
                try
                {
                    Singleton<FikaServer>.Instance.SendDataToPeer(peer, ref packet, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex)
                {
                   
                }
            }
            else if (Singleton<FikaClient>.Instantiated)
            {
               
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }

        }

        private static void OnPlayerPositionPacketReceived(PlayerPositionPacket packet, NetPeer peer)
        {
            if (Singleton<FikaServer>.Instantiated && FikaBackendUtils.IsHeadless)
            {
                SendPlayerPositionPacket(packet.playerId, packet.timeOfDeath, packet.position);
            }
            else
            {
                RMSession.AddToCriticalPlayers(packet.playerId, packet.position);
                //FikaHealthBar fikaHealthBar = Singleton<FikaHealthBar>.Instance;
                //PlayerPlateUI playerPlateUI = Singleton<PlayerPlateUI>.Instance;
                
                //playerPlateUI.SetNameText("Revive your teammate if you can!");
            }
        }
        private static void OnRemovePlayerFromCriticalPlayersListPacketReceived(RemovePlayerFromCriticalPlayersListPacket packet,  NetPeer peer)
        {
            if (Singleton<FikaServer>.Instantiated && FikaBackendUtils.IsHeadless)
            {
                SendRemovePlayerFromCriticalPlayersListPacket(packet.playerId);
            }
            else
            { 
                RMSession.RemovePlayerFromCriticalPlayers(packet.playerId);
            }
        }
        private static void OnReviveMePacketReceived(ReviveMePacket packet, NetPeer peer)
        {
            if (Singleton<FikaServer>.Instantiated && FikaBackendUtils.IsHeadless)
            {
                SendReviveMePacket(packet.reviveeId, packet.reviverId);
            }
            else { 
                bool revived = Features.RevivalFeatures.TryPerformRevivalByTeammate(packet.reviveeId);
                if (revived)
                {
                    SendRevivedPacket(packet.reviverId, peer);
                }
            }
        }

        private static void OnRevivedPacketReceived(RevivedPacket packet, NetPeer peer)
        {
            if (Singleton<FikaServer>.Instantiated && FikaBackendUtils.IsHeadless)
            {
                SendRevivedPacket(packet.reviverId, peer);
            }
            else { 
                NotificationManagerClass.DisplayMessageNotification(
                        $"Succesfully revived your teammate!",
                        ENotificationDurationType.Long,
                        ENotificationIconType.Friend,
                        Color.green);
            }

        }

        public static void OnFikaNetManagerCreated(FikaNetworkManagerCreatedEvent managerCreatedEvent)
        {
            managerCreatedEvent.Manager.RegisterPacket<PlayerPositionPacket, NetPeer>(OnPlayerPositionPacketReceived);
            managerCreatedEvent.Manager.RegisterPacket<RemovePlayerFromCriticalPlayersListPacket, NetPeer>(OnRemovePlayerFromCriticalPlayersListPacketReceived);
            managerCreatedEvent.Manager.RegisterPacket<ReviveMePacket, NetPeer>(OnReviveMePacketReceived);
            managerCreatedEvent.Manager.RegisterPacket<RevivedPacket, NetPeer>(OnRevivedPacketReceived);
        }

        public static void InitOnPluginEnabled()
        {        
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnFikaNetManagerCreated);
        }
    }
}