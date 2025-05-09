﻿using Microsoft.AspNetCore.SignalR;
using Solvix.Server.Core.Entities;
using Solvix.Server.Application.DTOs;
using Solvix.Server.Application.Helpers;
using Solvix.Server.Core.Interfaces;
using Solvix.Server.API.Hubs;
using Microsoft.VisualBasic;

namespace Solvix.Server.Application.Services
{
    public class ChatService : IChatService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly IUserConnectionService _userConnectionService;
        private readonly ILogger<ChatService> _logger;

        public ChatService(
            IUnitOfWork unitOfWork,
            IHubContext<ChatHub> hubContext,
            IUserConnectionService userConnectionService,
            ILogger<ChatService> logger)
        {
            _unitOfWork = unitOfWork;
            _hubContext = hubContext;
            _userConnectionService = userConnectionService;
            _logger = logger;
        }

        public async Task<List<ChatDto>> GetUserChatsAsync(long userId)
        {
            try
            {
                var chats = await _unitOfWork.ChatRepository.GetUserChatsAsync(userId);

                var participantIds = chats
                    .SelectMany(c => c.Participants)
                    .Select(p => p.UserId)
                    .Where(id => id != userId)
                    .Distinct()
                    .ToList();

                var onlineStatuses = new Dictionary<long, bool>();

                foreach (var id in participantIds)
                {
                    onlineStatuses[id] = await _userConnectionService.IsUserOnlineAsync(id);
                }

                onlineStatuses[userId] = true;

                var chatDtos = chats.Select(chat => MappingHelper.MapToChatDto(chat, userId, onlineStatuses)).ToList();


                return chatDtos;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chats for user {UserId}", userId);
                return new List<ChatDto>();
            }
        }

        public async Task<ChatDto?> GetChatByIdAsync(Guid chatId, long userId)
        {
            try
            {
                _logger.LogInformation("Fetching chat {ChatId} for user {UserId}", chatId, userId);
                var chat = await _unitOfWork.ChatRepository.GetChatWithParticipantsAsync(chatId);
                if (chat == null)
                {
                    _logger.LogWarning("Chat {ChatId} not found.", chatId);
                    return null;
                }

                if (!await IsUserParticipantAsync(chatId, userId))
                {
                    _logger.LogWarning("User {UserId} is not a participant of chat {ChatId}.", userId, chatId);
                    return null;
                }

                var participantIds = chat.Participants.Select(p => p.UserId).Distinct().ToList();
                var onlineStatuses = new Dictionary<long, bool>();
                foreach (var id in participantIds)
                {
                    onlineStatuses[id] = await _userConnectionService.IsUserOnlineAsync(id);
                }

                var chatDto = MappingHelper.MapToChatDto(chat, userId, onlineStatuses);
                _logger.LogInformation("Returning ChatDto for chat {ChatId}", chatId);
                return chatDto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chat {ChatId} for user {UserId}", chatId, userId);
                return null;
            }
        }

        public async Task<(Guid chatId, bool alreadyExists)> StartChatWithUserAsync(long initiatorUserId, long recipientUserId)
        {
            if (initiatorUserId == recipientUserId) throw new InvalidOperationException("Cannot start a chat with yourself");
            try
            {
                var existingChat = await _unitOfWork.ChatRepository.GetPrivateChatBetweenUsersAsync(initiatorUserId, recipientUserId);
                if (existingChat != null) return (existingChat.Id, true);

                var chat = new Chat { IsGroup = false, CreatedAt = DateTime.UtcNow };
                await _unitOfWork.ChatRepository.AddAsync(chat);
                await _unitOfWork.ChatRepository.AddParticipantAsync(chat.Id, initiatorUserId);
                await _unitOfWork.ChatRepository.AddParticipantAsync(chat.Id, recipientUserId);
                await _unitOfWork.CompleteAsync();
                _logger.LogInformation("New chat created between {User1} and {User2}. ChatId: {ChatId}", initiatorUserId, recipientUserId, chat.Id);
                return (chat.Id, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting chat between {User1} and {User2}", initiatorUserId, recipientUserId); throw;
            }
        }

        public async Task<Message> SaveMessageAsync(Guid chatId, long senderId, string content)
        {
            if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Message content cannot be empty.");

            var isParticipant = await IsUserParticipantAsync(chatId, senderId);
            if (!isParticipant)
            {
                _logger.LogWarning("User {UserId} attempted to send message to Chat {ChatId} without being a participant.", senderId, chatId);
                throw new UnauthorizedAccessException("User is not a participant of this chat.");
            }

            var newMessage = new Message
            {
                ChatId = chatId,
                SenderId = senderId,
                Content = content,
                SentAt = DateTime.UtcNow,
                IsRead = false,
                ReadAt = null
            };

            await _unitOfWork.MessageRepository.AddAsync(newMessage);

            try
            {
                await _unitOfWork.CompleteAsync();
                _logger.LogInformation("Message {MessageId} saved for Chat {ChatId} by User {UserId}", newMessage.Id, chatId, senderId);
                return newMessage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save message to database for Chat {ChatId} by User {UserId}.", chatId, senderId);
                throw;
            }
        }

        public async Task BroadcastMessageAsync(Message message)
        {
            if (message == null || message.Id <= 0)
            {
                _logger.LogError("BroadcastMessageAsync called with invalid message or message ID.");
                throw new ArgumentNullException(nameof(message), "Message or message ID cannot be null/invalid for broadcasting.");
            }

            try
            {
                var sender = await _unitOfWork.UserRepository.GetByIdAsync(message.SenderId);
                if (sender == null)
                {
                    _logger.LogError("Cannot broadcast message {MessageId}. Sender user {UserId} not found.", message.Id, message.SenderId);
                    return;
                }
                var senderFullName = $"{sender.FirstName} {sender.LastName}".Trim();

                var chat = await _unitOfWork.ChatRepository.GetChatWithParticipantsAsync(message.ChatId);
                if (chat == null)
                {
                    _logger.LogError("Cannot broadcast message {MessageId}. Chat {ChatId} not found.", message.Id, message.ChatId);
                    return;
                }
                var participantIds = chat.Participants.Select(p => p.UserId).ToList();

                var messageDto = MappingHelper.MapToMessageDto(message);

                foreach (var participantId in participantIds)
                {
                    var connectionIds = await _userConnectionService.GetConnectionsForUserAsync(participantId);
                    foreach (var connectionId in connectionIds)
                    {
                        if (!string.IsNullOrEmpty(connectionId))
                        {
                            try
                            {
                                await _hubContext.Clients.Client(connectionId).SendAsync(
                                    "ReceiveMessage",
                                    messageDto
                                );
                                _logger.LogInformation("Sent message DTO (ID {MessageId}) to User {UserId} on connection {ConnectionId}",
                                    messageDto.Id, participantId, connectionId);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error sending message DTO (ID {MessageId}) to User {UserId} on connection {ConnectionId}",
                                    messageDto.Id, participantId, connectionId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting message {MessageId}", message.Id);
            }
        }

        public async Task<List<MessageDto>> GetChatMessagesAsync(Guid chatId, long userId, int skip = 0, int take = 50)
        {
            try
            {
                if (!await IsUserParticipantAsync(chatId, userId))
                {
                    _logger.LogWarning("User {UserId} unauthorized attempt to get messages for Chat {ChatId}.", userId, chatId);
                    throw new UnauthorizedAccessException("User is not a participant of this chat.");
                }

                var messages = await _unitOfWork.MessageRepository.GetChatMessagesAsync(chatId, skip, take);

                var messageDtos = messages.Select(MappingHelper.MapToMessageDto).ToList();
                _logger.LogInformation("Returning {Count} messages for chat {ChatId}", messageDtos.Count, chatId);
                return messageDtos;
            }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting messages for chat {ChatId} by user {UserId}", chatId, userId);
                return new List<MessageDto>();
            }
        }

        public async Task MarkMessageAsReadAsync(int messageId, long readerUserId)
        {
            try
            {
                // Get the message and ensure the reader is a participant and not the sender
                var message = await _unitOfWork.MessageRepository.GetByIdAsync(messageId);
                if (message == null || message.SenderId == readerUserId)
                {
                    _logger.LogWarning("MarkMessageAsReadAsync: Message {MessageId} not found or sender is the reader {UserId}.", messageId, readerUserId);
                    return; // Ignore if message not found or sender tries to mark own message read
                }

                if (!await IsUserParticipantAsync(message.ChatId, readerUserId))
                {
                    _logger.LogWarning("MarkMessageAsReadAsync: User {UserId} is not a participant of chat {ChatId}.", readerUserId, message.ChatId);
                    return; // Ignore if user is not in the chat
                }

                // Mark the message as read in the repository
                await _unitOfWork.MessageRepository.MarkAsReadAsync(messageId, readerUserId); // Pass readerUserId for validation inside repo if needed
                await _unitOfWork.CompleteAsync();
                _logger.LogInformation("Message {MessageId} marked as read by user {UserId}.", messageId, readerUserId);


                // Notify the *sender* that the message was read
                var senderConnections = await _userConnectionService.GetConnectionsForUserAsync(message.SenderId);
                foreach (var connectionId in senderConnections)
                {
                    if (!string.IsNullOrEmpty(connectionId))
                    {
                        await _hubContext.Clients.Client(connectionId).SendAsync(
                            "MessageStatusChanged",
                            message.ChatId,
                            messageId,
                            Constants.MessageStatus.Read // Send the correct status code
                        );
                        _logger.LogDebug("Sent MessageRead notification for MessageId {MessageId} to Sender {SenderId} on connection {ConnectionId}", messageId, message.SenderId, connectionId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking message {MessageId} as read by user {UserId}", messageId, readerUserId);
                // Consider how to handle this error (e.g., retry logic, specific exception types)
            }
        }

        public async Task MarkMultipleMessagesAsReadAsync(List<int> messageIds, long readerUserId)
        {
            if (messageIds == null || !messageIds.Any()) return;

            try
            {
                // Fetch messages to get sender IDs and ChatId (assume all IDs are for the same chat for simplicity here)
                // Optimize: Fetch only necessary info if performance is critical
                var messages = await _unitOfWork.MessageRepository.ListAsync(m => messageIds.Contains(m.Id));
                if (!messages.Any()) return;

                var validMessageIds = messages
                    .Where(m => m.SenderId != readerUserId && !m.IsRead) // Filter out own messages and already read ones
                    .Select(m => m.Id)
                    .ToList();

                if (!validMessageIds.Any())
                {
                    _logger.LogInformation("MarkMultipleMessagesAsReadAsync: No valid messages found to mark as read for user {UserId}.", readerUserId);
                    return;
                }

                // Assume all messages are from the same chat - validate if necessary
                var chatId = messages.First().ChatId;
                if (!await IsUserParticipantAsync(chatId, readerUserId))
                {
                    _logger.LogWarning("MarkMultipleMessagesAsReadAsync: User {UserId} is not a participant of chat {ChatId}.", readerUserId, chatId);
                    return;
                }

                // Mark messages as read in the repository
                await _unitOfWork.MessageRepository.MarkMultipleAsReadAsync(validMessageIds, readerUserId);
                await _unitOfWork.CompleteAsync();
                _logger.LogInformation("Marked {Count} messages as read by user {UserId} in chat {ChatId}.", validMessageIds.Count, readerUserId, chatId);


                // Notify relevant senders
                var senderGroups = messages
                    .Where(m => validMessageIds.Contains(m.Id)) // Only consider messages that were actually marked read
                    .GroupBy(m => m.SenderId);

                foreach (var group in senderGroups)
                {
                    var senderId = group.Key;
                    var senderConnections = await _userConnectionService.GetConnectionsForUserAsync(senderId);
                    var readMessageIdsInGroup = group.Select(m => m.Id).ToList();

                    foreach (var connectionId in senderConnections)
                    {
                        if (!string.IsNullOrEmpty(connectionId))
                        {
                            // Send status update for each message individually
                            foreach (var messageId in readMessageIdsInGroup)
                            {
                                await _hubContext.Clients.Client(connectionId).SendAsync(
                                    "MessageStatusChanged",
                                    chatId,
                                    messageId,
                                    Constants.MessageStatus.Read
                                );
                            }
                            _logger.LogDebug("Sent MessageRead notifications for {Count} messages to Sender {SenderId} on connection {ConnectionId}", readMessageIdsInGroup.Count, senderId, connectionId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking multiple messages as read by user {UserId}", readerUserId);
            }
        }

        public async Task<bool> IsUserParticipantAsync(Guid chatId, long userId)
        {
            return await _unitOfWork.ChatRepository.IsUserParticipantAsync(chatId, userId);
        }
    }
}