using System.Text;
using System.Text.RegularExpressions;
using Nuraherbex.Api.Data;

namespace Nuraherbex.Api.Services;

public sealed record GuideFacts(List<Product> Products, List<Order> Orders, decimal FreeShippingFrom, decimal ShippingFee);

public enum GuideLang { En, Ta, Hi }
public enum GuideTone { Standard, Short, Friendly, Formal }

public sealed record GuideStyle(GuideLang Lang, GuideTone Tone)
{
    public static readonly GuideStyle Default = new(GuideLang.En, GuideTone.Standard);
}

/// <summary>One drafted reply: <see cref="Key"/> says which standard answer it is, so it can be re-written in another tone or language.</summary>
public sealed record GuideReply(string Key, string Intent, string Text, bool NeedsReview, string? Note);

/// <summary>
/// The built-in reply guide: picks a professional standard answer from what the customer asked, and can re-write it
/// shorter, friendlier, more formal, or in Tamil or Hindi - all without an AI key. Only facts that are known are stated
/// (real prices, the real order); nothing about health is advised.
/// </summary>
public static class ReplyGuide
{
    // ---------------------------------------------------------------------
    // What did the customer ask?
    // ---------------------------------------------------------------------

    private static readonly string[] HealthWords = ["diabet", "sugar", "bp", "blood pressure", "pressure", "thyroid", "pregnan", "breastfeed", "tablet", "medicine", "medication", "side effect", "side-effect", "cure", "treat", "doctor", "disease", "cancer", "kidney", "liver", "heart", "allerg", "safe", "age", "dose", "dosage", "how many times", "empty stomach", "with milk", "result"];
    private static readonly string[] PriceWords = ["price", "cost", "rate", "how much", "offer", "discount", "mrp", "vilai", "evalo", "kitna", "kimat", "amount"];
    private static readonly string[] DeliveryWords = ["deliver", "shipping", "ship", "courier", "how long", "how many days", "when will", "pincode", "pin code", "location", "available in", "reach"];
    private static readonly string[] PayWords = ["cod", "cash on delivery", "payment", "pay ", "upi", "gpay", "phonepe", "card", "advance", "emi"];
    private static readonly string[] OrderWords = ["track", "status", "where is", "dispatch", "shipped", "awb", "parcel", "my order", "order status", "order id", "when will i get"];
    private static readonly string[] BuyWords = ["buy", "purchase", "want", "need", "send me", "book", "confirm", "interested", "take"];
    private static readonly string[] UseWords = ["how to use", "how to take", "usage", "use it", "consume", "ingredient", "contain", "made of", "what is in"];
    private static readonly string[] ComplaintWords = ["refund", "return", "complaint", "cheat", "fake", "not received", "damaged", "wrong", "worst", "bad", "cancel", "angry", "fraud"];
    private static readonly string[] GreetWords = ["hi", "hello", "hey", "hai", "vanakkam", "namaste", "good morning", "good evening", "good afternoon"];

    /// <summary>Whole-word start match (so "liver" does not fire inside "delivery"); short words must match as whole words.</summary>
    private static bool Has(string text, string[] words) => words.Any(w =>
    {
        var t = w.Trim();
        var escaped = Regex.Escape(t);
        return Regex.IsMatch(text, t.Length <= 4 ? @"\b" + escaped + @"\b" : @"\b" + escaped);
    });

    public static string Detect(string? question, GuideFacts f)
    {
        if (question is null) return "intro";
        var q = question.ToLowerInvariant();
        if (q.StartsWith("[sticker")) return "sticker";
        if (q.StartsWith("[") && q.EndsWith("]")) return "file";
        if (Has(q, ComplaintWords)) return "complaint";
        if (Has(q, HealthWords)) return "health";
        if (Has(q, OrderWords) && f.Orders.Count > 0) return "status";
        if (Has(q, PriceWords)) return "price";
        if (Has(q, DeliveryWords)) return "delivery";
        if (Has(q, PayWords)) return "payment";
        if (Has(q, UseWords)) return "product";
        if (Has(q, BuyWords)) return "order";
        if (Has(q, GreetWords) && q.Length < 25) return "greeting";
        return "general";
    }

    private static string Label(string key) => key switch
    {
        "intro" => "Introduction", "sticker" => "Sticker", "file" => "Shared a file", "complaint" => "Complaint", "health" => "Health question",
        "status" => "Order status", "price" => "Price question", "delivery" => "Delivery question", "payment" => "Payment question",
        "product" => "Product details", "order" => "Wants to order", "greeting" => "Greeting", _ => "General question",
    };

    // ---------------------------------------------------------------------
    // The standard answers
    // ---------------------------------------------------------------------

    public static string FirstName(string name)
    {
        var n = name.Replace("[Sample]", "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return n is null || char.IsDigit(n[0]) ? "there" : n;
    }

    public static GuideReply Compose(string key, string leadName, GuideFacts f, GuideStyle style)
    {
        var name = FirstName(leadName);
        var free = f.FreeShippingFrom.ToString("N0");
        var fee = f.ShippingFee.ToString("N0");
        var lines = string.Join("\n", f.Products.Take(4).Select(p => $"- {p.Name}: Rs {p.Price:N0}"));
        var o = f.Orders.FirstOrDefault();
        (string? Courier, string Awb)? awb = o is null || string.IsNullOrEmpty(o.ShiprocketAwb) || o.ShiprocketAwb.StartsWith("SR-PENDING-") ? null : (o.Courier, o.ShiprocketAwb);

        var review = key is "complaint" or "health" or "product" or "file" or "general";
        string? note = key switch
        {
            "complaint" => "Complaint or refund message: read it, then add what you can resolve.",
            "health" => "Health question: do not give medical advice. Add product details only from the label.",
            "product" => "Add ingredient or usage details only exactly as on the product label.",
            "file" => "The customer sent a photo, voice note or file: open it first, then reply to what it shows.",
            "general" => "No standard answer matched: write a specific reply.",
            _ => null,
        };

        var text = style.Lang switch
        {
            GuideLang.Ta => Tamil(key, name, free, fee, lines, o, awb),
            GuideLang.Hi => Hindi(key, name, free, fee, lines, o, awb),
            _ => English(key, name, free, fee, lines, o, awb),
        };
        if (style.Lang == GuideLang.En && style.Tone != GuideTone.Standard) text = ApplyTone(text, style.Tone, name);
        return new GuideReply(key, Label(key), text, review, note);
    }

    private static string English(string key, string name, string free, string fee, string lines, Order? o, (string? Courier, string Awb)? awb) => key switch
    {
        "intro" => $"Hello {name}, this is the Nura Herbex team 🌿 Thank you for your interest. How can we help you today?",
        "sticker" => $"Hello {name} 🙂 Thank you for your message. How can we help you today?",
        "file" => $"Hello {name}, thank you for sharing this. Could you tell us a little about what you need help with, so we can assist you better?",
        "complaint" => $"Hello {name}, we are sorry for the trouble. Please share your order number and a short description, and our team will look into it and get back to you at the earliest.",
        "health" => $"Hello {name}, thank you for asking. For any question about health conditions or medicines, we recommend you consult your doctor before using any supplement. Our team will share the product details and ingredient information with you shortly.",
        "status" => $"Hello {name}, your order {o!.Id} is currently: {o.DeliveryStatus}.{(awb is { } a ? $" It is with {a.Courier}, tracking number {a.Awb}." : "")} You can also track it any time on our website. Please let us know if you need anything else.",
        "price" => $"Hello {name}, thank you for your interest in Nura Herbex 🌿 Our current prices:\n{lines}\nDelivery is free above Rs {free}. Which pack would you like to go with?",
        "delivery" => $"Hello {name}, we deliver across India. Delivery is free above Rs {free} (otherwise Rs {fee}). Please share your pincode and we will confirm the delivery time for your area.",
        "payment" => $"Hello {name}, you can choose cash on delivery or pay online. Whichever is convenient for you - shall we go ahead with your order?",
        "product" => $"Hello {name}, thank you for asking. Please follow the usage instructions and ingredient list printed on the pack. Our team will share the full details with you here shortly.",
        "order" => $"Hello {name}, thank you! To place your order, please share:\n1. Your full name\n2. Delivery address with pincode\n3. The pack you would like\nWe will confirm the total and arrange dispatch right away.",
        "greeting" => $"Hello {name}, welcome to Nura Herbex 🌿 How can we help you today?",
        _ => $"Hello {name}, thank you for contacting Nura Herbex. Our team has received your message and will reply shortly.",
    };

    // Tamil and Hindi are written for these standard answers; have a Tamil / Hindi speaker on the team read them once.
    private static string Tamil(string key, string name, string free, string fee, string lines, Order? o, (string? Courier, string Awb)? awb) => key switch
    {
        "intro" => $"வணக்கம் {name}, Nura Herbex குழுவிலிருந்து தொடர்பு கொள்கிறோம் 🌿 உங்கள் ஆர்வத்திற்கு நன்றி. உங்களுக்கு எப்படி உதவலாம்?",
        "sticker" => $"வணக்கம் {name} 🙂 உங்கள் செய்திக்கு நன்றி. உங்களுக்கு எப்படி உதவலாம்?",
        "file" => $"வணக்கம் {name}, பகிர்ந்ததற்கு நன்றி. உங்களுக்கு என்ன உதவி தேவை என்பதை சிறிது சொல்ல முடியுமா? அப்போது சிறப்பாக உதவ முடியும்.",
        "complaint" => $"வணக்கம் {name}, ஏற்பட்ட சிரமத்திற்கு வருந்துகிறோம். உங்கள் ஆர்டர் எண்ணையும் சிக்கலின் சிறு விவரத்தையும் பகிருங்கள். எங்கள் குழு உடனடியாகப் பார்த்து உங்களைத் தொடர்பு கொள்ளும்.",
        "health" => $"வணக்கம் {name}, கேட்டதற்கு நன்றி. உடல்நலம் அல்லது மருந்துகள் தொடர்பான கேள்விகளுக்கு, எந்த சப்ளிமெண்டையும் பயன்படுத்தும் முன் உங்கள் மருத்துவரை அணுகுமாறு பரிந்துரைக்கிறோம். தயாரிப்பு மற்றும் பொருட்கள் பற்றிய விவரங்களை எங்கள் குழு விரைவில் பகிரும்.",
        "status" => $"வணக்கம் {name}, உங்கள் ஆர்டர் {o!.Id} தற்போதைய நிலை: {o.DeliveryStatus}.{(awb is { } a ? $" இது {a.Courier} மூலம் அனுப்பப்படுகிறது, டிராக்கிங் எண்: {a.Awb}." : "")} மேலும் உதவி தேவைப்பட்டால் தெரிவிக்கவும்.",
        "price" => $"வணக்கம் {name}, Nura Herbex பற்றி விசாரித்ததற்கு நன்றி 🌿 தற்போதைய விலைகள்:\n{lines}\nRs {free}-க்கு மேல் ஆர்டர்களுக்கு டெலிவரி இலவசம். எந்த பேக் வேண்டும் என்று சொல்லுங்கள்.",
        "delivery" => $"வணக்கம் {name}, இந்தியா முழுவதும் டெலிவரி செய்கிறோம். Rs {free}-க்கு மேல் டெலிவரி இலவசம் (இல்லையெனில் Rs {fee}). உங்கள் பின்கோடைப் பகிருங்கள், உங்கள் பகுதிக்கான டெலிவரி நேரத்தை உறுதி செய்து தெரிவிக்கிறோம்.",
        "payment" => $"வணக்கம் {name}, கேஷ் ஆன் டெலிவரி அல்லது ஆன்லைன் பேமெண்ட் - உங்களுக்கு வசதியானதைத் தேர்வு செய்யலாம். உங்கள் ஆர்டரை உறுதி செய்யலாமா?",
        "product" => $"வணக்கம் {name}, கேட்டதற்கு நன்றி. பேக்கில் அச்சிடப்பட்டுள்ள பயன்பாட்டு வழிமுறைகள் மற்றும் பொருட்கள் பட்டியலைப் பின்பற்றவும். முழு விவரங்களை எங்கள் குழு விரைவில் இங்கே பகிரும்.",
        "order" => $"வணக்கம் {name}, நன்றி! உங்கள் ஆர்டரைப் பதிவு செய்ய, தயவுசெய்து இவற்றைப் பகிருங்கள்:\n1. உங்கள் முழு பெயர்\n2. பின்கோடுடன் டெலிவரி முகவரி\n3. நீங்கள் விரும்பும் பேக்\nமொத்தத் தொகையை உறுதி செய்து உடனே அனுப்பி வைக்கிறோம்.",
        "greeting" => $"வணக்கம் {name}, Nura Herbex-க்கு வரவேற்கிறோம் 🌿 உங்களுக்கு எப்படி உதவலாம்?",
        _ => $"வணக்கம் {name}, Nura Herbex-ஐத் தொடர்பு கொண்டதற்கு நன்றி. உங்கள் செய்தி கிடைத்தது; எங்கள் குழு விரைவில் பதிலளிக்கும்.",
    };

    private static string Hindi(string key, string name, string free, string fee, string lines, Order? o, (string? Courier, string Awb)? awb) => key switch
    {
        "intro" => $"नमस्ते {name}, हम Nura Herbex टीम से बात कर रहे हैं 🌿 आपकी रुचि के लिए धन्यवाद। हम आपकी कैसे मदद कर सकते हैं?",
        "sticker" => $"नमस्ते {name} 🙂 आपके संदेश के लिए धन्यवाद। हम आपकी कैसे मदद कर सकते हैं?",
        "file" => $"नमस्ते {name}, साझा करने के लिए धन्यवाद। कृपया थोड़ा बताएँ कि आपको किस बारे में मदद चाहिए, ताकि हम बेहतर सहायता कर सकें।",
        "complaint" => $"नमस्ते {name}, असुविधा के लिए हमें खेद है। कृपया अपना ऑर्डर नंबर और समस्या का संक्षिप्त विवरण भेजें, हमारी टीम जल्द से जल्द देखकर आपसे संपर्क करेगी।",
        "health" => $"नमस्ते {name}, पूछने के लिए धन्यवाद। स्वास्थ्य या दवाओं से जुड़े किसी भी प्रश्न के लिए, कोई भी सप्लीमेंट लेने से पहले कृपया अपने डॉक्टर से सलाह लें। हमारी टीम जल्द ही उत्पाद और सामग्री की जानकारी साझा करेगी।",
        "status" => $"नमस्ते {name}, आपका ऑर्डर {o!.Id} अभी: {o.DeliveryStatus}।{(awb is { } a ? $" यह {a.Courier} के पास है, ट्रैकिंग नंबर: {a.Awb}।" : "")} और किसी मदद की ज़रूरत हो तो बताइए।",
        "price" => $"नमस्ते {name}, Nura Herbex में रुचि दिखाने के लिए धन्यवाद 🌿 हमारी मौजूदा कीमतें:\n{lines}\nRs {free} से ऊपर के ऑर्डर पर डिलीवरी मुफ़्त है। आप कौन सा पैक लेना चाहेंगे?",
        "delivery" => $"नमस्ते {name}, हम पूरे भारत में डिलीवरी करते हैं। Rs {free} से ऊपर डिलीवरी मुफ़्त है (अन्यथा Rs {fee})। कृपया अपना पिनकोड भेजें, हम आपके क्षेत्र के लिए डिलीवरी समय की पुष्टि करेंगे।",
        "payment" => $"नमस्ते {name}, आप कैश ऑन डिलीवरी या ऑनलाइन भुगतान, जो भी आपको सुविधाजनक हो, चुन सकते हैं। क्या हम आपका ऑर्डर आगे बढ़ाएँ?",
        "product" => $"नमस्ते {name}, पूछने के लिए धन्यवाद। कृपया पैक पर छपे उपयोग निर्देश और सामग्री सूची देखें। हमारी टीम पूरी जानकारी जल्द यहाँ साझा करेगी।",
        "order" => $"नमस्ते {name}, धन्यवाद! ऑर्डर देने के लिए कृपया ये जानकारी भेजें:\n1. आपका पूरा नाम\n2. पिनकोड सहित डिलीवरी का पता\n3. आप कौन सा पैक लेना चाहते हैं\nहम कुल राशि की पुष्टि कर तुरंत भेजने की व्यवस्था करेंगे।",
        "greeting" => $"नमस्ते {name}, Nura Herbex में आपका स्वागत है 🌿 हम आपकी कैसे मदद कर सकते हैं?",
        _ => $"नमस्ते {name}, Nura Herbex से संपर्क करने के लिए धन्यवाद। आपका संदेश हमें मिल गया है, हमारी टीम जल्द ही जवाब देगी।",
    };

    // ---------------------------------------------------------------------
    // Change requests ("shorter", "friendlier", "more formal", "Tamil", "Hindi")
    // ---------------------------------------------------------------------

    /// <summary>short | friendly | formal | ta | hi, or null when the request is something only the AI can do.</summary>
    public static string? Classify(string? instruction)
    {
        var i = (instruction ?? "").ToLowerInvariant();
        if (i.Contains("tamil")) return "ta";
        if (i.Contains("hindi")) return "hi";
        if (i.Contains("short") || i.Contains("concise") || i.Contains("brief")) return "short";
        if (i.Contains("friend") || i.Contains("warm")) return "friendly";
        if (i.Contains("formal") || i.Contains("professional")) return "formal";
        return null;
    }

    private static readonly Regex GreetingRegex = new(
        @"^(Hello|Hi|Dear|Namaste)\s+(?<n>[^,!\n]+?)\s*[,!]\s*|^(Hello|Hi|Dear|Namaste)\s+(?<n>[^\s,!\n]+)\s+(?=[☀-➿\uD83C-\uDBFF])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmojiRegex =new(@"[☀-➿️]|[\uD83C-\uDBFF][\uDC00-\uDFFF]", RegexOptions.Compiled);

    /// <summary>Re-writes English text in another tone. Works on any text, including what the team member has already edited.</summary>
    public static string ApplyTone(string text, GuideTone tone, string name)
    {
        var t = text.Trim();
        switch (tone)
        {
            case GuideTone.Short:
            {
                // Whole sentences that only add politeness, and thank-you openers that can be removed from the front of a sentence.
                var dropSentences = new[] { "we are sorry for the trouble", "please let us know if you need anything else", "you can also track it", "this is the nura herbex team" };
                var openers = new Regex(@"^(thank you|thanks)( so much)? for (your interest in nura herbex|your interest|contacting nura herbex|asking|sharing this|your message)[.,!:]?(\s|[\uD83C-\uDBFF][\uDC00-\uDFFF]|️)*", RegexOptions.IgnoreCase);
                var kept = new List<string>();
                foreach (var line in t.Split('\n'))
                {
                    if (line.TrimStart().StartsWith('-') || Regex.IsMatch(line.TrimStart(), @"^\d+[.)]")) { kept.Add(line); continue; } // price and step lists stay whole
                    var m = GreetingRegex.Match(line);
                    var greeting = m.Success ? $"Hi {m.Groups["n"].Value.Trim()}, " : "";
                    var rest = m.Success ? line[m.Length..] : line;
                    var sentences = Regex.Split(rest.Trim(), @"(?<=[.!?])\s+").Where(s => s.Length > 0).ToList();
                    var essential = new List<string>();
                    foreach (var s in sentences)
                    {
                        if (dropSentences.Any(d => s.ToLowerInvariant().Contains(d))) continue;
                        var stripped = openers.Replace(s, "").Trim();
                        if (stripped.Length == 0) continue;
                        essential.Add(char.ToUpperInvariant(stripped[0]) + stripped[1..]);
                    }
                    if (essential.Count == 0) essential = sentences.TakeLast(1).ToList();
                    var shortened = string.Join(" ", essential.Take(2).Concat(essential.Count > 2 ? essential.TakeLast(1) : []).Distinct());
                    if (shortened.StartsWith("Our current prices")) shortened = "here are our current prices" + shortened["Our current prices".Length..];
                    else if (greeting.Length > 0 && shortened.Length > 0 && char.IsUpper(shortened[0]) && !shortened.StartsWith("We ") && !shortened.StartsWith("It ") && !shortened.StartsWith("Please") && !shortened.StartsWith("Our ")) shortened = char.ToLowerInvariant(shortened[0]) + shortened[1..];
                    kept.Add(greeting + shortened);
                }
                return string.Join("\n", kept).Trim();
            }
            case GuideTone.Friendly:
            {
                if (EmojiRegex.IsMatch(t) && t.Contains("Happy to help")) return t;
                var m = GreetingRegex.Match(t);
                if (m.Success)
                {
                    var after = t[m.Length..];
                    if (after.Length > 0) after = char.ToUpperInvariant(after[0]) + after[1..];
                    t = $"Hi {m.Groups["n"].Value.Trim()}! 😊 " + after;
                }
                t = Regex.Replace(t, @"\b[Tt]hank you for\b", "Thanks so much for");
                t = Regex.Replace(t, @"\bPlease share\b", "Could you please share");
                t = Regex.Replace(t, @"\bwe recommend\b", "we would gently suggest", RegexOptions.IgnoreCase);
                if (!t.Contains("Happy to help")) t += "\nHappy to help anytime 🌿";
                return t;
            }
            default: // Formal
            {
                // An emoji often ended a sentence ("...Nura Herbex 🌿 Our prices"): leave a full stop where it was.
                t = Regex.Replace(t, @"(?<=[\p{L}\p{N}])[ \t]*(?:[☀-➿️]|[\uD83C-\uDBFF][\uDC00-\uDFFF])+[ \t]*(?=[A-Z])", ". ");
                t = EmojiRegex.Replace(t, "");
                t = Regex.Replace(t, @"[ \t]{2,}", " ");
                t = Regex.Replace(t, @"[ \t]+(?=[.,!?])", "");
                var m = GreetingRegex.Match(t);
                if (m.Success) t = $"Dear {m.Groups["n"].Value.Trim()}, " + t[m.Length..];
                t = Regex.Replace(t, @"\bThanks\b", "Thank you").Replace("we're", "we are").Replace("We're", "We are").Replace("don't", "do not").Replace("can't", "cannot").Replace("you'll", "you will").Replace("it's", "it is");
                if (!t.Contains("Kind regards")) t += "\n\nKind regards,\nNura Herbex Team";
                return t.Replace(" \n", "\n").Trim();
            }
        }
    }

    /// <summary>Lower-case, single-spaced text used to recognise a draft the guide wrote earlier.</summary>
    public static string Normalize(string? text) => Regex.Replace((text ?? "").Trim().ToLowerInvariant(), @"\s+", " ");
}
