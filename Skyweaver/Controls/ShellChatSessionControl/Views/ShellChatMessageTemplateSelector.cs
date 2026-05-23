using System.Windows;
using System.Windows.Controls;
using Skyweaver.Controls.ShellChatSessionControl.Models;

namespace Skyweaver.Controls.ShellChatSessionControl.Views
{
    /// <summary>
    /// 根据消息角色选择对应数据模板的选择器
    /// </summary>
    public class ShellChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? UserTemplate { get; set; }
        public DataTemplate? AssistantTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is not ShellChatMessageModel message)
            {
                return base.SelectTemplate(item, container);
            }

            return message.Role switch
            {
                "User" => UserTemplate,
                "Assistant" => AssistantTemplate,
                _ => UserTemplate
            };
        }
    }
}
