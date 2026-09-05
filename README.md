
#  翼览无余（Aircraft MR）

> 基于 Unity 与 PICO MR 开发的航空科普交互平台。
> 
> 本项目围绕 C919、歼-20、直-20、运-20 等航空器，结合 MR 展示、模型交互、部件拆解、时间轴展示、视频课程与语音问答等方式，构建沉浸式航空科普体验。
<img width="3840" height="2160" alt="d82392f1ee725b08ea01f6e928f4e253" src="https://github.com/user-attachments/assets/2bf7093e-82cf-4f1b-aee7-95662d6eae44" />
---

# 项目简介

翼览无余（Aircraft MR）是一套基于 **Unity + PICO 4 Ultra Mixed Reality** 开发的航空科普教育平台。

项目通过 MR（Mixed Reality）技术，将航空器三维模型、部件拆解、时间轴、视频课程、AI语音问答等内容融合，打造沉浸式航空教育体验。

目前已完成多个国产航空器数字资源建设，包括：

- C919 大型客机
- 歼-20 隐身战斗机
- 运-20 大型运输机
- 直-20 通用直升机

项目主要面向：

- 高校航空教学
- 航空科普展馆
- MR/XR 教学展示
- 科研与课程开发

---

# 功能介绍

目前已实现：

- 飞机模型浏览
- 双手旋转模型
- 模型缩放
- 飞机爆炸图展示
- 部件拆解介绍
- 时间轴交互
- 视频课程播放
- 新闻资料展示
- AI 语音问答
- 多机型切换
- MR 场景交互
- PICO 手势交互

---

# 开发环境

| 项目 | 版本 |
|------|------|
| Unity | 2022.3.22f1 |
| 开发平台 | Windows |
| MR设备 | PICO 4 Ultra |
| PICO XR SDK | 3.4 |
| XR Interaction Toolkit | 2.6.4 |
| XR Hands | 1.8.0 |
| OpenXR | Enabled |
| Git | Git + Git LFS |

---

# 快速开始

## 1、克隆项目

```bash
git clone https://github.com/你的用户名/Aircraft-MR.git
```

---

## 2、安装环境

需要安装：

- Unity Hub
- Unity 2022.3.22f1
- Android Build Support
- OpenJDK
- Android SDK
- Android NDK

---

## 3、安装 Git LFS

```bash
git lfs install
```

项目中 FBX、MP4 等大文件均通过 Git LFS 管理。

---

## 4、打开项目

使用 Unity Hub：

Open Project

选择项目目录即可。

首次打开需要等待 Package 自动导入。

---

## 5、连接 PICO

连接 PICO 4 Ultra

Build And Run

即可体验 MR 内容。

---

# 项目目录

```text
Assets
│
├── 3D资产/
│   ├── C919
│   ├── 歼20
│   ├── 运20
│   └── 直20
│
├── Plane_VFX/
│   飞机尾焰与特效资源
│
├── Samples/
│   XR Toolkit 示例
│
├── 公用文件/
│   Logo、公共UI、素材
│
├── 翼览无余/
│   项目核心资源
│   ├── Scenes
│   ├── Scripts
│   ├── UI
│   ├── Video
│   ├── Audio
│   ├── Images
│   └── Prefabs
│
Packages/
│
ProjectSettings/
```

---

# 已支持机型

| 机型 | 状态 |
|------|------|
| C919 | ✅ |
| 歼-20 | ✅ |
| 运-20 | ✅ |
| 直-20 | ✅ |

---

# 核心模块

## 飞机展示

- 飞机整体浏览
- 双手旋转
- 双手缩放

---

## 爆炸图

支持：

- 一键展开
- 一键复原
- 部件浏览
- 部件介绍

---

## 时间轴

支持：

- 飞机发展历史
- 时间节点介绍
- 图片展示
- 视频展示

---

## AI语音问答

支持：

- 航空知识问答
- 飞机介绍
- AI语音识别
- AI回答

---

## 视频课程

支持：

- 航空课程
- 新闻资料
- 图片播放
- 视频播放

---

# Git LFS

项目使用 Git LFS 管理大文件。

当前跟踪：

```text
*.fbx
*.mp4
*.webm
*.tif
*.tiff
*.psd
```

安装：

```bash
git lfs install
```

查看：

```bash
git lfs ls-files
```

---

# 开发规范

命名规范：

- PascalCase（类）
- camelCase（变量）
- UI统一命名
- Prefab统一命名
- Scene统一命名

Git：

- 每完成一个功能提交一次 Commit
- 大文件使用 Git LFS
- 不提交 Library
- 不提交 Temp

---

# 使用插件

项目主要使用：

- PICO XR SDK
- XR Interaction Toolkit
- XR Hands
- OpenXR
- TextMeshPro
- Git LFS

---

# Roadmap

## 已完成

- [x] C919
- [x] 歼20
- [x] 运20
- [x] 直20
- [x] MR展示
- [x] 爆炸图
- [x] AI语音问答
- [x] 时间轴

## 开发中

- [ ] 空警2000
- [ ] 长空一号
- [ ] 红专一号
- [ ] 飞行模拟模块
- [ ] 更多国产航空器

---

# License

仅供学习、科研及教学使用。

未经许可，不得用于商业用途。

---

# 作者

项目名称：

**翼览无余（Aircraft MR）**

开发平台：

Unity + PICO Mixed Reality

GitHub：

https://github.com/你的用户名/Aircraft-MR
