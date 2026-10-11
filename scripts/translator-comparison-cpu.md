# 翻译引擎对比

生成时间：2026-10-11 18:44

| 引擎 | 加载 | 单句中位延迟 | 单句最大延迟 |
|---|---|---|---|
| argos (cpu) | 6.0 s | 0.084 s | 0.166 s |
| nllb-600m (cpu) | 2.0 s | 0.478 s | 0.76 s |
| hymt2-1.8b (cpu) | 3.2 s | 0.824 s | 5.01 s |
| hymt2-7b (cpu) | 5.9 s | 2.606 s | 4.373 s |

## 日语 · 日常对话

**ねえ、昨日のこと覚えてる？**

- argos (cpu)：你还记得昨天吗?
- nllb-600m (cpu)：你还记得昨天?
- hymt2-1.8b (cpu)：喂，你还记得昨天发生的事情吗？
- hymt2-7b (cpu)：喂，还记得昨天的事吗？

**うん、まさかあんなことになるとは思わなかったよ。**

- argos (cpu)：我觉得这不会很烦人。
- nllb-600m (cpu)：我没想到会发生这样的事情.
- hymt2-1.8b (cpu)：嗯，真没想到会发展成那样。
- hymt2-7b (cpu)：嗯，真没想到会变成那样。

**でもさ、結局は先輩のおかげで助かったんだよね。**

- argos (cpu)：然而,毕竟,这是对老年人的帮助。
- nllb-600m (cpu)：但事实上,我们最终得到了帮助.
- hymt2-1.8b (cpu)：不过，最终还是多亏了前辈的帮忙才得以幸免于难啊。
- hymt2-7b (cpu)：不过啊，说到底还是多亏了学长，我们才得救的吧。

**今度ちゃんとお礼言わなきゃ。**

- argos (cpu)：我必须再次感谢你。
- nllb-600m (cpu)：现在我必须感谢你.
- hymt2-1.8b (cpu)：这次得好好道谢才行。
- hymt2-7b (cpu)：下次一定要好好道谢才行。


## 日语 · 动画台词

**お前なんかに負けるわけねえだろ！**

- argos (cpu)：你会失去它!
- nllb-600m (cpu)：你不会输任何东西!
- hymt2-1.8b (cpu)：我可不会输给像你这样的家伙啊！
- hymt2-7b (cpu)：我怎么可能输给这种人！

**諦めるのはまだ早い。最後まで戦うんだ。**

- argos (cpu)：现在放弃还为时过早。 我战斗到最后。
- nllb-600m (cpu)：现在还很早放弃.
- hymt2-1.8b (cpu)：放弃还为时过早。要战斗到最后一刻。
- hymt2-7b (cpu)：现在放弃还为时过早。要战斗到最后一刻。

**この力…まさか、封印が解けたのか？**

- argos (cpu)：这种力量... 密封了吗?
- nllb-600m (cpu)：这种力量... 封锁破了吗?
- hymt2-1.8b (cpu)：这股力量……难道，封印解开了吗？
- hymt2-7b (cpu)：这股力量……难不成，封印已经解开了吗？


## 日语 · 新闻

**政府は来年度から新たな少子化対策を実施すると発表しました。**

- argos (cpu)：政府宣布从明年起将执行新的解密措施。
- nllb-600m (cpu)：政府宣布将从明年开始采取新的措施.
- hymt2-1.8b (cpu)：政府宣布，从下一年开始实施新的生育政策。
- hymt2-7b (cpu)：政府宣布，从下个财政年度开始将实施新的少子化对策。

**東京都内では今日、最高気温が三十五度を超える猛暑日となりました。**

- argos (cpu)：今天,温度超过30摄氏度。
- nllb-600m (cpu)：在东京,今天的最高气温超过35摄氏度.
- hymt2-1.8b (cpu)：今天，东京都内的最高气温超过了35度，出现了酷热天气。
- hymt2-7b (cpu)：今天，东京都出现了高温天气，最高气温超过35度，属于酷热日。


## 日语 · 省略主语

**もう行かなきゃ。**

- argos (cpu)：我必须这样做。
- nllb-600m (cpu)：我必须走.
- hymt2-1.8b (cpu)：我得赶紧离开了。
- hymt2-7b (cpu)：该走了。

**えっ、もう？まだ来たばかりじゃん。**

- argos (cpu)：是吗? 我刚刚到
- nllb-600m (cpu)：⁇ ,我还没有来过.
- hymt2-1.8b (cpu)：啊，已经了吗？我才刚来不久呢。
- hymt2-7b (cpu)：啊，已经要走了？我们才刚到而已啊。

**ごめん、明日早いんだ。**

- argos (cpu)：对不起,明天很早
- nllb-600m (cpu)：我很抱歉,明天早些时候.
- hymt2-1.8b (cpu)：对不起，明天很早。
- hymt2-7b (cpu)：抱歉，明天要早起。


## 英语 · 日常对话

**Hey, are you coming to the party tonight?**

- argos (cpu)：嘿,你今晚来参加派对吗?
- nllb-600m (cpu)：你今晚会来聚会吗?
- hymt2-1.8b (cpu)：嘿，你今晚会来参加派对吗？
- hymt2-7b (cpu)：嘿，你今晚会来参加派对吗？

**I don't know, I've got a ton of work to catch up on.**

- argos (cpu)：我不知道,我还有好多工作要做
- nllb-600m (cpu)：我不知道,我有很多工作要赶上.
- hymt2-1.8b (cpu)：我不知道，我有很多工作需要赶工完成。
- hymt2-7b (cpu)：我不知道，我有好多工作要补做。

**Come on, it's Friday. You can't be serious.**

- argos (cpu)：来,今天是星期五 你不可能是认真的。
- nllb-600m (cpu)：现在是星期五,你不能说真的.
- hymt2-1.8b (cpu)：拜托，今天是星期五。你不会是认真的吧。
- hymt2-7b (cpu)：得了吧，今天是周五啊。你不会是认真的吧。


## 英语 · 影视台词

**We're running out of time. The whole city is counting on us.**

- argos (cpu)：我们没时间了 整个城市都指望着我们
- nllb-600m (cpu)：我们没有时间,整个城市都依靠我们.
- hymt2-1.8b (cpu)：我们的时间已经不多了。整个城市都依赖于我们。
- hymt2-7b (cpu)：我们的时间所剩无几了。整个城市都在期待着我们。

**I never asked for any of this.**

- argos (cpu)：我从来没有要求任何这一切。
- nllb-600m (cpu)：我从来没有要求过.
- hymt2-1.8b (cpu)：我从未要求过这些。
- hymt2-7b (cpu)：我从未要求过这一切。

**Whatever happens, don't look back.**

- argos (cpu)：无论发生什么,不要回头看。
- nllb-600m (cpu)：无论发生什么,不要回头看.
- hymt2-1.8b (cpu)：无论发生什么，都不要回头。
- hymt2-7b (cpu)：无论发生什么，都不要回头。


## 英语 · 新闻/讲解

**The central bank decided to keep interest rates unchanged for the third consecutive month.**

- argos (cpu)：中央银行决定连续三个月保持利率不变.
- nllb-600m (cpu)：中央银行决定连续第三个月保持利率不变.
- hymt2-1.8b (cpu)：中央银行决定连续第三个月保持利率不变。
- hymt2-7b (cpu)：央行决定连续第三个月保持利率不变。

**In this video, we'll walk through how to set up the development environment step by step.**

- argos (cpu)：在这段视频中,我们将走过如何一步一步地建立开发环境.
- nllb-600m (cpu)：在这段视频中,我们将一步步介绍如何设置开发环境.
- hymt2-1.8b (cpu)：在这段视频中，我们将逐步介绍如何设置开发环境。
- hymt2-7b (cpu)：在这段视频中，我们将一步步讲解如何搭建开发环境。
