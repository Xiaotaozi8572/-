翼览无余

基于 Unity 与 PICO MR 设备开发的航空科普交互项目。

本项目围绕 C919、歼-20、直-20、运-20 等航空器，结合 MR 展示、模型交互、部件拆解、时间轴展示、视频课程与语音问答等方式，构建沉浸式航空科普体验。

---

 项目简介

“翼览无余”是一套面向航空科普展示场景的 MR 交互系统。

项目通过三维航空器模型、MR 空间交互、部件拆解、知识信息展示及多媒体内容，将传统图文航空知识转化为空间化、交互化的学习体验。

主要功能包括：

- 航空器三维模型浏览
- MR 空间展示
- 手势与控制器交互
- 飞机模型旋转与观察
- 航空器爆炸图展示
- 部件单独查看与介绍
- 航空发展时间轴
- 视频课程与新闻资料展示
- 航空知识语音问答
- 多机型内容切换

---

开发环境

- Unity：2022.3.22f1
- 开发平台：Windows
- 目标设备：PICO 4 Ultra
- PICO XR SDK：3.4
- XR Interaction Toolkit：2.6.4
- XR Hands：1.8.0
- OpenXR

---

项目目录

```text
Assets/
├── 3D资产/               航空器模型、部件模型及相关材质
├── Plane_VFX/            飞行特效相关资源
├── Samples/              XR Interaction Toolkit / XR Hands 示例与依赖
├── 翼揽无余/             项目主要 UI、脚本、视频及交互资源
└── 公用文件/             项目共用图片、片头等资源

Packages/
├── manifest.json
├── packages-lock.json
└── pico包/               PICO XR SDK 相关内容

ProjectSettings/
└── Unity 项目配置文件
