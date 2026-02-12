using System.Windows;
using System.Windows.Controls;
using SimplyShare.Models;

namespace SimplyShare.Converters;

/// <summary>
/// ChatMessage 타입/방향에 따라 적절한 DataTemplate 선택
/// </summary>
public sealed class ChatMessageTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not ChatMessage message || container is not FrameworkElement element)
            return null;

        var key = (message.Type, message.Direction) switch
        {
            (ChatMessageType.Text, ChatDirection.Sent) => "SentTextTemplate",
            (ChatMessageType.Text, ChatDirection.Received) => "ReceivedTextTemplate",
            (ChatMessageType.File, ChatDirection.Sent) => "SentFileTemplate",
            (ChatMessageType.File, ChatDirection.Received) => "ReceivedFileTemplate",
            (ChatMessageType.System, _) => "SystemTemplate",
            _ => "SentTextTemplate"
        };

        return element.TryFindResource(key) as DataTemplate
            ?? System.Windows.Application.Current.MainWindow?.TryFindResource(key) as DataTemplate;
    }
}
