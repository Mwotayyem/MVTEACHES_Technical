# أسئلة MEPS — جاهزة للإرسال

| | |
|---|---|
| **الغرض** | كل ما يجب معرفته من MEPS **قبل أي التزام** وقبل كتابة سطر كود واحد |
| **التاريخ** | 2026-08-24 |
| **الشروط المعروضة** | **30 د.أ شهريًا** · **2.5%** على كل حركة · **+0.10 د.أ** على كل حركة |
| **التحليل الكامل** | [`MVTEACHES_Payments_And_Reporting_Study.md`](MVTEACHES_Payments_And_Reporting_Study.md) §1 |
| **كيف تستعمله** | ⭐ **الجزء الأول يُرسَل كما هو** · والجزء الثالث **لك وحدك** — لا تُرسله |

---

# 🟢 الجزء الأول — الرسالة الجاهزة للإرسال (عربي)

> **انسخ من هنا وأرسله لهم كما هو.**

---

**الموضوع: استفسارات فنية ومالية قبل التعاقد — منصة تعليمية أونلاين**

تحية طيبة،

نحن بصدد ربط منصة تعليمية أونلاين ببوابة دفع لاستقبال مدفوعات الطلاب بالبطاقات. وصلتنا الشروط الأولية (اشتراك شهري 30 د.أ · عمولة 2.5% · 0.10 د.أ على كل حركة)، ونحتاج التوضيحات التالية **مع الوثائق الفنية (API Documentation)** قبل اتخاذ القرار:

### أولًا — طريقة الربط

1. هل الدفع يتم عبر **صفحة دفع مستضافة لديكم** (يُحوَّل إليها العميل ثم يعود)، أم عبر **API مباشر** تُرسَل إليه بيانات البطاقة من خوادمنا؟
2. إن كان الخياران متاحين، **ما الفروقات في الرسوم والمتطلبات بينهما؟**
3. ما متطلبات الامتثال (PCI DSS) المترتبة علينا في كل خيار؟

### ثانيًا — الرسوم بالتفصيل

4. هل نسبة **2.5%** تنطبق على **البطاقات الصادرة في الأردن فقط**؟ وما النسبة على **البطاقات الأجنبية** (خليجية · أوروبية · أمريكية)؟
5. هل تختلف النسبة بين **Visa و Mastercard** أو حسب نوع البطاقة (Debit / Credit / Prepaid)؟
6. هل الاشتراك الشهري **30 د.أ شاملة الضريبة العامة على المبيعات** أم تُضاف عليها؟
7. هل يُخصم الاشتراك الشهري في شهر **لا توجد فيه أي حركة**؟
8. هل هناك **رسم تأسيس** أو **حد أدنى شهري** أو **رسم سنوي** أو أي رسم آخر غير المذكور أعلاه؟

### ثالثًا — العملات والتوريد

9. هل نستطيع التحصيل بـ **الدولار الأمريكي** و **الشيكل**، أم بالدينار الأردني فقط؟ وإن كان بالدينار فقط، **من يتحمّل فرق التحويل** ومن يحدد سعر الصرف؟
10. **مدة التوريد (Settlement):** كم يومًا حتى يصل المبلغ إلى حسابنا البنكي؟ وهل التوريد يومي أم أسبوعي؟

### رابعًا — الاسترداد والنزاعات

11. هل يوجد **API للاسترداد (Refund)**؟ وهل يدعم الاسترداد **الجزئي** إضافة إلى الكلي؟
12. عند الاسترداد، **هل تُعاد إلينا عمولة الـ2.5% والـ0.10، أم تبقى مستحقة عليكم؟**
13. ما **رسم النزاع (Chargeback)**؟ وما إجراءات الاعتراض والمدة المتاحة لنا؟
14. هل **3-D Secure** متاح؟ وهل تفعيله إلزامي؟ و**من يتحمّل مسؤولية النزاع** مع تفعيله ومن دونه؟

### خامسًا — التقنية

15. هل يوجد **Webhook / Callback** لتأكيد نجاح العملية؟ وهل هو **موقَّع رقميًا**؟ وبأي خوارزمية توقيع؟
16. هل **معرّف العملية (Transaction ID)** فريد وثابت؟ *(نحتاجه لمنع احتساب الدفعة مرتين إذا تكرر الإشعار)*
17. هل توفّرون **بيئة اختبار (Sandbox)** وبطاقات تجريبية؟
18. هل يوجد دعم لـ **حفظ البطاقة (Tokenization)** للدفعات المتكررة مستقبلًا؟

### سادسًا — المتطلبات القانونية

19. هل يلزم **سجل تجاري باسم شركة**، أم يمكن التعاقد بحساب/سجل شخصي؟
20. ما **المستندات المطلوبة** بالضبط، و**كم تستغرق** إجراءات التفعيل من تاريخ تقديمها؟

⭐ ونرجو تزويدنا بـ **وثائق الـ API** و **نموذج العقد** للاطلاع عليهما قبل الالتزام.

مع الشكر والتقدير،

---

# 🔵 الجزء الثاني — النسخة الإنجليزية

> ⭐ **أرسل هذه أيضًا إن كان تواصلك مع قسم فني** — الوثائق التقنية غالبًا بالإنجليزية، والإجابة تكون أدق.

---

**Subject: Technical & Commercial Questions Before Onboarding — Online Education Platform**

Hello,

We are integrating an online education platform with a payment gateway to collect student payments by card. We have received your preliminary terms (JOD 30 monthly subscription, 2.5% per transaction, plus JOD 0.10 per transaction). Before proceeding, we need the following clarifications **along with your API documentation**:

**Integration model**
1. Is payment handled via a **hosted payment page** on your side (redirect and return), or via a **direct API** where card data is sent from our servers?
2. If both are available, what are the differences in fees and requirements?
3. What **PCI DSS** scope applies to us under each model?

**Fees**
4. Does the **2.5%** apply to Jordanian-issued cards only? What is the rate for **foreign-issued cards** (GCC, Europe, US)?
5. Does the rate differ between **Visa and Mastercard**, or by card type (debit / credit / prepaid)?
6. Is the JOD 30 monthly fee **inclusive of sales tax**, or is tax added?
7. Is the monthly fee charged in a month with **zero transactions**?
8. Are there any **setup fees, monthly minimums, annual fees**, or other charges not listed above?

**Currencies & settlement**
9. Can we charge in **USD** and **ILS**, or JOD only? If JOD only, who bears the conversion difference and who sets the exchange rate?
10. What is the **settlement period** to our bank account? Is settlement daily or weekly?

**Refunds & disputes**
11. Is there a **refund API**? Does it support **partial** refunds as well as full?
12. On refund, are the 2.5% and JOD 0.10 **returned to us, or retained**?
13. What is the **chargeback fee**, and what is the dispute process and our response window?
14. Is **3-D Secure** available? Is it mandatory? Who bears **chargeback liability** with and without it?

**Technical**
15. Do you provide a **webhook / callback** confirming a successful payment? Is it **cryptographically signed**, and with which algorithm?
16. Is the **transaction ID** unique and stable? *(We need it for idempotency, to avoid crediting a payment twice on duplicate notifications.)*
17. Do you provide a **sandbox environment** and test cards?
18. Do you support **card tokenization** for future recurring payments?

**Legal & onboarding**
19. Is a **registered company** required, or can we contract as a sole proprietor / individual?
20. What **documents** are required, and how long does activation take from submission?

Please also share your **API documentation** and a **draft contract** for review.

Thank you.

---

# 🔴 الجزء الثالث — لك وحدك · ⛔ لا تُرسل هذا الجزء

**ما الذي تغيّره كل إجابة فعليًا:**

## ⭐⭐ الأسئلة الثلاثة التي تحسم القرار

| السؤال | إن كانت الإجابة… | الأثر |
|---|---|---|
| **1 — صفحة مستضافة أم API مباشر؟** | ✅ **صفحة مستضافة** | ⭐ **الوضع المطلوب.** بيانات البطاقة لا تلمس خوادمنا إطلاقًا ← أخف نطاق امتثال ممكن |
| | 🔴 **API مباشر فقط** | ⛔ **تصير أنت مسؤولًا عن حماية أرقام البطاقات.** عبء امتثال وتدقيق دوري **لا يحتمله مشروع بهذا الحجم** ➜ **ابحث عن مزوّد آخر** |
| **4 — نسبة البطاقات الأجنبية** | نفس 2.5% | ✅ التبرير قائم |
| | 🔴 أعلى بنقطة أو أكثر | ⚠️ **ينهار نصف التبرير** — لأن **طلابك الدوليين هم سبب أخذ البوابة أصلًا** |
| **19 — سجل تجاري؟** | — | ⭐ **هذا هو `Q-01`** الموصوف في الدراسة بأنه «السؤال المحجوب رقم واحد في المشروع كله». ⭐ **إجابتهم تغلقه سواء تعاقدت معهم أم لا** |

## 💰 الأسئلة التي تغيّر الحساب المالي

| السؤال | لماذا يهم |
|---|---|
| **6 — الضريبة** | الفرق قد يبلغ **خُمس الرسم**. بند يُكتشف عادةً على **أول فاتورة** لا في العرض الأولي |
| **7 — شهر بلا حركة** | ⭐ الإجابة شبه المؤكدة «نعم يُخصم» — **وهذا سبب تأجيل التفعيل** حتى يوجد حجم يبرّره |
| **8 — رسوم خفية** | العروض الأولية نادرًا ما تكون كاملة |
| **12 — عمولة الاسترداد** | ⚠️ الشائع أنها **تُخسر**، وأحيانًا تُحتسب مرتين. مع `D-15` (لا استرداد) الأثر محدود، لكن اعرفه قبل أن تحتاجه |
| **10 — مدة التوريد** | يمس **السيولة لا الربح** — لكنه يقرر متى تدفع أجور المعلمين |

## 🔧 الأسئلة التي يفشل الربط بدونها

| السؤال | الأثر التقني |
|---|---|
| **15 — Webhook موقَّع** | §21.6 يفرض تأكيد الدفع بالـWebhook **حصرًا**. ⛔ **ولا webhook بلا توقيع** — بدونه يستطيع أي أحد تزوير إشعار «تم الدفع» |
| **16 — معرّف فريد وثابت** | نموذجنا فيه `UNIQUE (provider_key, provider_txn_id)` ⭐ **لمنع اعتماد الدفعة مرتين** عند تكرار الإشعار |
| **17 — Sandbox** | ⛔ **بلا بيئة اختبار لا يُختبر مسار الدفع أصلًا** — ولن نطلق نظامًا ماليًا بلا اختبار |
| **9 — العملات** | `D-53` بنى تسعيرًا بثلاث عملات. إن كان التحصيل بالدينار فقط، فكل عملية دولية فيها تحويل — ⭐ **وسعر صرفه يجب أن يُختَم على الدفعة** (§4.3) |

## ⛔ ثلاثة أمور لا تفعلها قبل وصول الإجابات

| # | |
|---|---|
| 1 | ❌ **لا توقّع عقدًا** قبل قراءة إجابة السؤال 1 |
| 2 | ❌ **لا تُفعِّل الاشتراك الشهري** — الـ30 د.أ تبدأ من يوم التفعيل لا من يوم أول عملية |
| 3 | ❌ **لا تكتب سطر كود ربط** قبل وصول الوثائق والـSandbox |

> ⭐ **وتذكّر الخلاصة:** المعمارية جاهزة لاستقبالهم متى شئت (`countries.payment_provider_key` ← صنف واحد جديد · صفر تعديل في منطق الأعمال). **فالتأجيل لا يكلّفك شيئًا هندسيًا — والتعجّل يكلّفك 30 دينارًا شهريًا بلا مقابل.**

---

**آخر تحديث:** 2026-08-24 · مرافق لـ [`MVTEACHES_Payments_And_Reporting_Study.md`](MVTEACHES_Payments_And_Reporting_Study.md)
