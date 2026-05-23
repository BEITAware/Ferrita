using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Skyweaver.Infrastructure.Mvvm;
using Skyweaver.Commands;
using Skyweaver.Controls.ShellChatSessionControl.Models;

namespace Skyweaver.Controls.ShellChatSessionControl.ViewModels
{
    /// <summary>
    /// Shell聊天会话的ViewModel
    /// </summary>
    public class ShellChatSessionControlViewModel : ObservableObject
    {
        private string _inputText = string.Empty;
        
        /// <summary>
        /// 消息列表集合
        /// </summary>
        public ObservableCollection<ShellChatMessageModel> Messages { get; } = new ObservableCollection<ShellChatMessageModel>();

        /// <summary>
        /// 用户当前的输入文本
        /// </summary>
        public string InputText
        {
            get => _inputText;
            set => SetProperty(ref _inputText, value);
        }

        /// <summary>
        /// 发送消息命令
        /// </summary>
        public ICommand SendCommand { get; }

        /// <summary>
        /// 清空历史记录命令
        /// </summary>
        public ICommand ClearCommand { get; }

        /// <summary>
        /// 关闭/隐藏窗口命令
        /// </summary>
        public ICommand CloseCommand { get; }

        /// <summary>
        /// 请求关闭窗口的事件/委托回调
        /// </summary>
        public event Action? RequestClose;

        public ShellChatSessionControlViewModel()
        {
            SendCommand = new RelayCommand(ExecuteSend, CanExecuteSend);
            ClearCommand = new RelayCommand(ExecuteClear);
            CloseCommand = new RelayCommand(ExecuteClose);

            LoadDemoData();
        }

        /// <summary>
        /// 加载演示用的占位数据
        /// </summary>
        private void LoadDemoData()
        {
            Messages.Add(new ShellChatMessageModel
            {
                Role = "Assistant",
                SenderName = "Skyweaver AI",
                Content = "你好！我是 Skyweaver 智能助理。我已嵌入到系统 Shell 中，随时准备为您提供帮助。\n\n您可以向我提问，或者让我协助您进行文件管理和工作流配置。",
                Timestamp = DateTime.Now.AddMinutes(-5),
                AvatarPath = "pack://application:,,,/Resources/GuideBot.png"
            });

            Messages.Add(new ShellChatMessageModel
            {
                Role = "User",
                SenderName = "User",
                Content = "请问如何快速配置一个新的 Aerial City 渲染管线节点？",
                Timestamp = DateTime.Now.AddMinutes(-3),
                AvatarPath = "pack://application:,,,/Resources/QuestionBot.png"
            });

            Messages.Add(new ShellChatMessageModel
            {
                Role = "Assistant",
                SenderName = "Skyweaver AI",
                Content = "配置 Aerial City 节点非常简单，您可以按照以下步骤操作：\n\n1. 在主界面左侧的 **文件树** 中选择您的项目文件夹。\n2. 点击顶部工具栏的 **「新建图表」** 按钮。\n3. 从节点库中拖拽 **AerialCityRenderNode** 至编辑区。\n4. 在右侧属性面板中绑定您的 LUT 颜色映射图。\n\n您也可以直接在当前 Shell 中输入命令来自动完成初始化。",
                Timestamp = DateTime.Now.AddMinutes(-2),
                AvatarPath = "pack://application:,,,/Resources/GuideBot.png"
            });
        }

        private bool CanExecuteSend()
        {
            return !string.IsNullOrWhiteSpace(InputText);
        }

        private void ExecuteSend()
        {
            if (string.IsNullOrWhiteSpace(InputText)) return;

            // 添加用户发送的消息
            Messages.Add(new ShellChatMessageModel
            {
                Role = "User",
                SenderName = "User",
                Content = InputText,
                Timestamp = DateTime.Now,
                AvatarPath = "pack://application:,,,/Resources/QuestionBot.png"
            });

            string userQuery = InputText;
            InputText = string.Empty;

            // 模拟助手回复（占位延迟）
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                await System.Threading.Tasks.Task.Delay(800);
                
                string replyContent = $"收到您的请求：\"{userQuery}\"。这是一个占位回复示例，在实际版本中，这里将对接本地大语言模型，并结合 Skyweaver 代理执行链。";
                
                Messages.Add(new ShellChatMessageModel
                {
                    Role = "Assistant",
                    SenderName = "Skyweaver AI",
                    Content = replyContent,
                    Timestamp = DateTime.Now,
                    AvatarPath = "pack://application:,,,/Resources/GuideBot.png"
                });
            }));
        }

        private void ExecuteClear()
        {
            Messages.Clear();
            Messages.Add(new ShellChatMessageModel
            {
                Role = "Assistant",
                SenderName = "Skyweaver AI",
                Content = "历史记录已清空。有什么我可以帮您的？",
                Timestamp = DateTime.Now,
                AvatarPath = "pack://application:,,,/Resources/GuideBot.png"
            });
        }

        private void ExecuteClose()
        {
            RequestClose?.Invoke();
        }
    }
}
