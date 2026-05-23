using System;

namespace Skyweaver.Controls.ShellChatSessionControl.Models
{
    /// <summary>
    /// Shell聊天消息的数据模型
    /// </summary>
    public class ShellChatMessageModel
    {
        /// <summary>
        /// 消息的唯一标识
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 发送者名称
        /// </summary>
        public string SenderName { get; set; } = string.Empty;

        /// <summary>
        /// 角色 ("User" 或 "Assistant")
        /// </summary>
        public string Role { get; set; } = "User";

        /// <summary>
        /// 消息内容
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// 时间戳
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>
        /// 头像路径
        /// </summary>
        public string AvatarPath { get; set; } = string.Empty;
    }
}
