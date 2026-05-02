<div align="center">

# BEITA Skyweaver [编织我们曾拥有的未来]

![License](https://img.shields.io/badge/license-MIT%20%2F%20Unlicense-blue)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
![Framework](https://img.shields.io/badge/framework-WPF%20%7C%20.NET-512BD4)
![Status](https://img.shields.io/badge/status-Active%20Development-success)

<!-- HERO IMAGE PLACEHOLDER: Insert a beautiful screenshot of Skyweaver's Aero UI here -->
<img src="./assets/hero-placeholder.png" width="800" alt="Skyweaver Hero Image Placeholder">

**开源的，用户友好的，安全的OpenClaw替代品。使用Aero设计风格，与Windows的美好功能深度融合。**

</div>

> Skyweaver摒弃了漆黑的CLI窗口或是单调的扁平化页面，将千禧年代的人们对未来人工智能的畅想转化为了现实。

---

## ✅ 目前已实现的功能

* **美观的WPF控制台应用程序**：控制代理的各项行为。
* **侧向文件系统 (Lateral File System)**：利用Windows ProjFS API实现的虚拟工作区，所有读写需求被自动转发至投影工作区。如果修改满意，用户可将投影工作区合并至原工作区并销毁投影。如果修改出现问题，用户可回滚投影工作区而丝毫不影响原文件。
* **安全的Skills (Toolkit)**：Toolkit是一套工具集合，模型可在代理循环操作期间主动加载或卸载这些工具集合。以便在上下文可控的情况下扩展工具调用能力。
* **XML Priority**：应用程序几乎所有配置文件、上下文处理、Tool Calling和结构化输出都使用XML实现。XML是强大的结构化标记语言，能够比JSON更清晰地传递语义。
* **属于所有人的代理智能体**：项目以MIT/Unlicense双协议开源，确保不会遭到任何单一著作权人控制。项目如同所有BEITAware软件一样是免费的，不收分文即可使用。记得带上API秘钥。
* **BEITAware 荣誉制造**：BEITAware是一个技术美术家团队，曾制造了强大的Tunnel和Cascade用于多媒体处理（[Cascade](https://github.com/BEITAware/Cascade)、[Tunnel](https://github.com/BEITAware/Tunnel)）。Skyweaver继承了这一卓越的多媒体领域积累，在Shell集成中针对素材管理与转码等需求深度优化，具有替代FFmpeg Shell应用程序的潜质。

---

## 🚀 宏伟蓝图与开发计划 (Roadmap)

以下激动人心的功能正在紧锣密鼓地筹备与开发中：

* **多重模型配置**：可配备多个不同层级不同知识面的语言模型，也可以配备不同收费标准的模型以节约开销。
* **Shell集成**：集成于Explorer右键菜单，允许以自然语言完成复杂的文件操作。
* **桌面助理球**：允许用户通过语音、点击、敲击等方式呼出代理。
* **Web集成**：模型可通过搜索API或者Playwright MCP访问互联网。模型也可以通过在Hyper-V中进行Computer Use以更方便快速地访问互联网。
* **任务计划唤醒**：创建任务计划程序来定期唤醒助理执行特定任务。
* **公文包互联**：Agent主会话上下文可在非Windows平台流转，用户可以培养和Agent的感情和记忆。在非Windows平台和Windows平台互联后通过公文包机制将上下文重新同步回Windows平台，并开始执行用户先前可能设定过的任务。
* **.NET工具集**：模型可访问Powershell或Roslyn Script作为Shell/脚本环境，利用其丰富功能帮助用户代理Windows计算机的操作。
* **Motion动作合集**：为节约Token，模型可以自行将一些重复性任务转化为Roslyn/Powershell脚本动作合集，触发这些动作合集来完成重复性操作。
* **Skyweaver叠加层**：通过快捷键呼出类似Windows截屏页面的叠加层，点选或截取屏幕部分传递给助理，附加语音或文本提升，快速获取帮助。
* **纸质友好**：应用程序深度集成传真与打印功能，可将配置文件、会话记录、代理运行历史打印到纸上或通过传真发送给他人。
* **会话流SessionFlow系统**：代理可在可视化节点图编辑器内被编排。应用程序自带最简单的ChatGPT类型的会话流Chat和严谨而强大的多代理会话流Dynasty。Dynasty涉及计划代理、审阅代理、执行规划者代理和六个执行子代理共9个代理，且通过强大的上下文感知系统将同任务开销控制在Chat的300%，远低于600%的预期值。

---

## 🎨 主题系统

当前的主题名为 **Aero 3**，是Microsoft主导的设计风格Aero/Aero 2的延续。我们正在扩展更多可能的主题，目前已排上日程的有：

* **Candy**：甜蜜的粉色系可爱主题，具有糖果风味和实色渐变样式。
* **Bliss**：蓝天、白云、绿草地，旨在复刻您对千禧年计算机记忆的一切。
* **Pride**：带有缤纷彩虹元素的主题。
* **Expression Pro**：实色简约风黑灰色系主题，带有黄橙色强调色。
* **Bureau Blue**：实色蓝色系主题，带有黄色强调色，具有WPF企业级应用程序的典型风格。

---

## 📜 开源协议

本项目采用 **MIT** / **Unlicense** 双协议开源。
