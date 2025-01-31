﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// SemanticKernelChatMessageContentConnector.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AutoGen.SemanticKernel;

/// <summary>
/// This middleware converts the incoming <see cref="IMessage"/> to <see cref="ChatMessageContent"/> before passing to agent.
/// And converts the reply message from <see cref="ChatMessageContent"/> to <see cref="IMessage"/> before returning to the caller.
/// 
/// <para>requirement for agent</para>
/// <para>- Input message type: <see cref="IMessage{T}"/> where T is <see cref="ChatMessageContent"/></para>
/// <para>- Reply message type: <see cref="IMessage{T}"/> where T is <see cref="ChatMessageContent"/></para>
/// <para>- (streaming) Reply message type: <see cref="IMessage{T}"/> where T is <see cref="StreamingChatMessageContent"/></para>
/// 
/// This middleware supports the following message types:
/// <para>- <see cref="TextMessage"/></para>
/// <para>- <see cref="ImageMessage"/></para>
/// <para>- <see cref="MultiModalMessage"/></para>
/// 
/// This middleware returns the following message types:
/// <para>- <see cref="TextMessage"/></para>
/// <para>- <see cref="ImageMessage"/></para>
/// <para>- <see cref="MultiModalMessage"/></para>
/// <para>- (streaming) <see cref="TextMessageUpdate"/></para>
/// </summary>
public class SemanticKernelChatMessageContentConnector : IMiddleware, IStreamingMiddleware
{
    public string? Name => nameof(SemanticKernelChatMessageContentConnector);

    public async Task<IMessage> InvokeAsync(MiddlewareContext context, IAgent agent, CancellationToken cancellationToken = default)
    {
        var messages = context.Messages;

        var chatMessageContents = ProcessMessage(messages, agent)
            .Select(m => new MessageEnvelope<ChatMessageContent>(m));
        var reply = await agent.GenerateReplyAsync(chatMessageContents, context.Options, cancellationToken);

        return PostProcessMessage(reply);
    }

    public Task<IAsyncEnumerable<IStreamingMessage>> InvokeAsync(MiddlewareContext context, IStreamingAgent agent, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(InvokeStreamingAsync(context, agent, cancellationToken));
    }

    private async IAsyncEnumerable<IStreamingMessage> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chatMessageContents = ProcessMessage(context.Messages, agent)
            .Select(m => new MessageEnvelope<ChatMessageContent>(m));

        await foreach (var reply in await agent.GenerateStreamingReplyAsync(chatMessageContents, context.Options, cancellationToken))
        {
            yield return PostProcessStreamingMessage(reply);
        }
    }

    private IMessage PostProcessMessage(IMessage input)
    {
        return input switch
        {
            IMessage<ChatMessageContent> messageEnvelope => PostProcessMessage(messageEnvelope),
            _ => throw new System.NotImplementedException(),
        };
    }

    private IStreamingMessage PostProcessStreamingMessage(IStreamingMessage input)
    {
        return input switch
        {
            IStreamingMessage<StreamingChatMessageContent> streamingMessage => PostProcessMessage(streamingMessage),
            IMessage msg => PostProcessMessage(msg),
            _ => throw new System.NotImplementedException(),
        };
    }

    private IMessage PostProcessMessage(IMessage<ChatMessageContent> messageEnvelope)
    {
        var chatMessageContent = messageEnvelope.Content;
        var items = chatMessageContent.Items.Select<KernelContent, IMessage>(i => i switch
        {
            TextContent txt => new TextMessage(Role.Assistant, txt.Text!, messageEnvelope.From),
            ImageContent img when img.Uri is Uri uri => new ImageMessage(Role.Assistant, uri.ToString(), from: messageEnvelope.From),
            ImageContent img when img.Uri is null => throw new InvalidOperationException("ImageContent.Uri is null"),
            _ => throw new InvalidOperationException("Unsupported content type"),
        });

        if (items.Count() == 1)
        {
            return items.First();
        }
        else
        {
            return new MultiModalMessage(Role.Assistant, items, from: messageEnvelope.From);
        }
    }

    private IStreamingMessage PostProcessMessage(IStreamingMessage<StreamingChatMessageContent> streamingMessage)
    {
        var chatMessageContent = streamingMessage.Content;
        if (chatMessageContent.ChoiceIndex > 0)
        {
            throw new InvalidOperationException("Only one choice is supported in streaming response");
        }
        return new TextMessageUpdate(Role.Assistant, chatMessageContent.Content, streamingMessage.From);
    }

    private IEnumerable<ChatMessageContent> ProcessMessage(IEnumerable<IMessage> messages, IAgent agent)
    {
        return messages.SelectMany(m =>
        {
            if (m is IMessage<ChatMessageContent> chatMessageContent)
            {
                return [chatMessageContent.Content];
            }
            if (m.From == agent.Name)
            {
                return ProcessMessageForSelf(m);
            }
            else
            {
                return ProcessMessageForOthers(m);
            }
        });
    }

    private IEnumerable<ChatMessageContent> ProcessMessageForSelf(IMessage message)
    {
        return message switch
        {
            TextMessage textMessage => ProcessMessageForSelf(textMessage),
            MultiModalMessage multiModalMessage => ProcessMessageForSelf(multiModalMessage),
            Message m => ProcessMessageForSelf(m),
            _ => throw new System.NotImplementedException(),
        };
    }

    private IEnumerable<ChatMessageContent> ProcessMessageForOthers(IMessage message)
    {
        return message switch
        {
            TextMessage textMessage => ProcessMessageForOthers(textMessage),
            MultiModalMessage multiModalMessage => ProcessMessageForOthers(multiModalMessage),
            ImageMessage imageMessage => ProcessMessageForOthers(imageMessage),
            Message m => ProcessMessageForOthers(m),
            _ => throw new InvalidOperationException("unsupported message type, only support TextMessage, ImageMessage, MultiModalMessage and Message."),
        };
    }

    private IEnumerable<ChatMessageContent> ProcessMessageForSelf(TextMessage message)
    {
        if (message.Role == Role.System)
        {
            return [new ChatMessageContent(AuthorRole.System, message.Content)];
        }
        else
        {
            return [new ChatMessageContent(AuthorRole.Assistant, message.Content)];
        }
    }


    private IEnumerable<ChatMessageContent> ProcessMessageForOthers(TextMessage message)
    {
        if (message.Role == Role.System)
        {
            return [new ChatMessageContent(AuthorRole.System, message.Content)];
        }
        else
        {
            return [new ChatMessageContent(AuthorRole.User, message.Content)];
        }
    }

    private IEnumerable<ChatMessageContent> ProcessMessageForOthers(ImageMessage message)
    {
        var imageContent = new ImageContent(new Uri(message.Url));
        var collectionItems = new ChatMessageContentItemCollection();
        collectionItems.Add(imageContent);
        return [new ChatMessageContent(AuthorRole.User, collectionItems)];
    }

    private IEnumerable<ChatMessageContent> ProcessMessageForSelf(MultiModalMessage message)
    {
        throw new System.InvalidOperationException("MultiModalMessage is not supported in the semantic kernel if it's from self.");
    }

    private IEnumerable<ChatMessageContent> ProcessMessageForOthers(MultiModalMessage message)
    {
        var collections = new ChatMessageContentItemCollection();
        foreach (var item in message.Content)
        {
            if (item is TextMessage textContent)
            {
                collections.Add(new TextContent(textContent.Content));
            }
            else if (item is ImageMessage imageContent)
            {
                collections.Add(new ImageContent(new Uri(imageContent.Url)));
            }
            else
            {
                throw new InvalidOperationException($"Unsupported message type: {item.GetType().Name}");
            }
        }
        return [new ChatMessageContent(AuthorRole.User, collections)];
    }


    private IEnumerable<ChatMessageContent> ProcessMessageForSelf(Message message)
    {
        if (message.Role == Role.System)
        {
            return [new ChatMessageContent(AuthorRole.System, message.Content)];
        }
        else if (message.Content is string && message.FunctionName is null && message.FunctionArguments is null)
        {
            return [new ChatMessageContent(AuthorRole.Assistant, message.Content)];
        }
        else if (message.Content is null && message.FunctionName is not null && message.FunctionArguments is not null)
        {
            throw new System.InvalidOperationException("Function call is not supported in the semantic kernel if it's from self.");
        }
        else
        {
            throw new System.InvalidOperationException("Unsupported message type");
        }
    }

    private IEnumerable<ChatMessageContent> ProcessMessageForOthers(Message message)
    {
        if (message.Role == Role.System)
        {
            return [new ChatMessageContent(AuthorRole.System, message.Content)];
        }
        else if (message.Content is string && message.FunctionName is null && message.FunctionArguments is null)
        {
            return [new ChatMessageContent(AuthorRole.User, message.Content)];
        }
        else if (message.Content is null && message.FunctionName is not null && message.FunctionArguments is not null)
        {
            throw new System.InvalidOperationException("Function call is not supported in the semantic kernel if it's from others.");
        }
        else
        {
            throw new System.InvalidOperationException("Unsupported message type");
        }
    }
}
