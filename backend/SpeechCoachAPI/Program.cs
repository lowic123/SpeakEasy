using Google.GenAI;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;




var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options => {
    options.AddPolicy("AllowAll", policy => {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 524288000; // 500 MB in bytes
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 524288000; // 500 MB in bytes
});


var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("no key found");
}
else
{
    Console.WriteLine("key loaded");
}

builder.Services.AddSingleton(new AudioClient("whisper-1", apiKey));
builder.Services.AddSingleton(new ChatClient("gpt-4o", apiKey));

var app = builder.Build();
app.UseCors("AllowAll");


app.UseStaticFiles();

app.MapGet("/", () => "Hello World!");
app.MapGet("/health", () => Results.Ok("ok"));

//generate behavioural question
app.MapGet("/generate-behavioural-question", async (HttpRequest request, [FromServices] ChatClient chat) => {
    var clientKey = request.Headers["x-api-key"].ToString();
    var validKey = Environment.GetEnvironmentVariable("CLIENT_API_KEY");
    if (!clientKey.Equals(validKey))
        return Results.Unauthorized();
    var result = await chat.CompleteChatAsync("Generate one challenging behavioral interview question. Only professionally answer with the question.");
    return Results.Ok(new { question = result.Value.Content[0].Text });
});

//generate tech question
app.MapGet("/generate-technical-question", async (HttpRequest request, [FromServices] ChatClient chat) => {
    var clientKey = request.Headers["x-api-key"].ToString();
    var validKey = Environment.GetEnvironmentVariable("CLIENT_API_KEY");
    if (!clientKey.Equals(validKey))
        return Results.Unauthorized();
    var result = await chat.CompleteChatAsync("Generate one challenging technical interview question for 1st or 2nd year software engineers. Only professionally answer with the question.");
    return Results.Ok(new { question = result.Value.Content[0].Text });
});

//technical
app.MapPost("/analyze-tech-interview", async (HttpRequest request, [FromServices] AudioClient audio, [FromServices] ChatClient chat) =>
{
    return await ProcessVoiceLogic(request, audio, chat, isInterview : true, isTechInterview: true);
});

//behaviour
app.MapPost("/analyze-interview", async (HttpRequest request, [FromServices] AudioClient audio, [FromServices] ChatClient chat) =>
{
    return await ProcessVoiceLogic(request, audio, chat, isInterview: true,isTechInterview: false);
});

//general speech
app.MapPost("/analyze", async (HttpRequest request, [FromServices] AudioClient audio, [FromServices] ChatClient chat) =>
{
    return await ProcessVoiceLogic(request, audio, chat, isInterview: false, isTechInterview: false);
});
    

app.Run();

async Task<IResult> ProcessVoiceLogic(HttpRequest request, AudioClient audio, ChatClient chat, bool isInterview, bool isTechInterview)
{
    var clientKey = request.Headers["x-api-key"].ToString();
    var validKey = Environment.GetEnvironmentVariable("CLIENT_API_KEY");
    if (!clientKey.Equals(validKey))
        return Results.Unauthorized();

    if (!request.HasFormContentType)
        return Results.BadRequest("Invalid data");
    var form = await request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    //var question = form["question"].ToString();
    //Console.WriteLine($"DEBUG: Received Question = '{question}'");

    if (file == null || file.Length == 0)
        return Results.BadRequest("Nothing uploaded");


    //Directory.CreateDirectory("Temp/videos");
    //var videoPath = Path.Combine("Temp/videos", Guid.NewGuid() + Path.GetExtension(file.FileName));
    //{
    //    using var stream = File.Create(videoPath);
    //    await file.CopyToAsync(stream);
    //}

    //Directory.CreateDirectory("Temp/audios");
    //string audioPath = Path.Combine("Temp/audios", Guid.NewGuid() + ".wav");
    string videoPath = Path.Combine("Temp/videos", Guid.NewGuid() + Path.GetExtension(file.FileName));
    string audioPath = Path.Combine("Temp/audios", Guid.NewGuid() + ".wav");

    try
    {
        Directory.CreateDirectory("Temp/videos");
        using (var stream = File.Create(videoPath))
            await file.CopyToAsync(stream);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{videoPath}\" -ar 16000 -ac 1 \"{audioPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();

        string errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            return Results.Problem($"FFmpeg Error: {errors}");


        var transcriptionOptions = new AudioTranscriptionOptions
        {
            TimestampGranularities = AudioTimestampGranularities.Word,
            ResponseFormat = AudioTranscriptionFormat.Verbose,
            Prompt = "Umm,let me think. Uh, like, I'm not sure, but ummm, maybe so."
        };
        ClientResult<AudioTranscription> transcriptionResult = await audio.TranscribeAudioAsync(audioPath, transcriptionOptions);
        AudioTranscription transcription = transcriptionResult.Value;

        double vidLength = await GetVideoDuration(videoPath);

        //filler words
        char[] separators = { ' ', ',', '.', '!', '?', ';', ':', '\n', '\r' };
        string text = transcription.Text;
        var words = text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        int totalWords = words.Length;
        string[] fillerWords = { "uh", "uhh", "um", "umm", "ummm", "like", "so", "basically", };
        int fillerCount = 0;
        foreach (string word in words)
        {
            if (fillerWords.Contains(word.ToLower()))
                fillerCount++;
        }

        //pauses
        double totalPauseTime = 0;
        int longPauseCount = 0;
        double pauseThreshold = 1;

        for (int i = 1; i < transcription.Words.Count; i++)
        {
            var prevWord = transcription.Words[i - 1];
            var currentWord = transcription.Words[i];
            double gap = currentWord.StartTime.TotalSeconds - prevWord.EndTime.TotalSeconds;
            if (gap > pauseThreshold)
            {
                totalPauseTime += gap;
                longPauseCount++;
                Console.WriteLine($"Pause detected: {gap:F2}s between '{prevWord.Word}' and '{currentWord.Word}'");
            }
        }


        string systemPrompt = isInterview ? isTechInterview ?
                                //$"This is was the question asked: {question}" +
                                "You are a professional software engineering interview coach. Analyze the user's answer to a technical interview question and provide feedback in STRICT JSON format." +
                                "Focus on feedback specific to this solution, not generic programming advice." +
                                "If the user does not answer the question, or has an answer too derived from the original question,do not evaluate any strengths" +
                                "First infer the problem-solving approach used (e.g., brute force, optimized, recursive, iterative) and evaluate strengths and weaknesses relative to the expected or optimal solution." +
                                "Assess correctness, efficiency, clarity of explanation, and trade-offs discussed, making the feedback personalized to the problem and approach chosen." +
                                "Only mention filler words if there are more than 3; if there are few or none, include this as a strength." +
                                "If the solution or explanation is incomplete, missing edge cases, or lacks complexity analysis, mark those categories as �insufficient data� instead of guessing and do not add any strengths." +
                                "If the speaking pace is significantly above 160 words per minute or below 140 words per minute, mention it in the tips category." +
                                "Mention stucture as a strength is th answer is well structured"
                               :
                               //$"This is was the question asked: {question}" +
                              "You are a professional interview coach. Analyze the user's answer to a behavioral interview question and provide feedback in STRICT JSON format." +
                              "Focus on feedback specific to this answer, not generic interview advice." +
                              "If the user does not answer the question, or has an answer too derived from the original question,do not evaluate any strengths" +
                              "Only mention filler words if there are more than 3; if there are few or none, include this as a strength." +
                              "First infer the answer structure (e.g., STAR, narrative, unstructured) and evaluate strengths and weaknesses relative to that structure." +
                              "Assess clarity, relevance, and impact of the example used, making the feedback personalized to the scenario described." +
                              "If the answer is too short or missing key elements (e.g., result or reflection), mark that category as �insufficient data� instead of guessing." +
                              "If the speaking pace is significantly above 160 words per minute or below 140 words per minute, mention it in the tips category."
                              :
                             "You are a professional speech coach. Analyze the user's speech and provide feedback in STRICT JSON format." +
                             "If the user does not answer the question, or has an answer too derived from the original question,do not evaluate any strengths" +
                              "Try to give tips that apply to the speech itself do not write tips that can be applied to any speech" +
                              "Only talk about filler words when there are more than 3, consider adding the lack of filler words to the strengths" +
                              "Give some strengths and weaknesses based on the nature of the speech so that it is more personalized" +
                              "First infer the speech type (e.g., persuasive, informative, narrative, casual) and evaluate strengths and weaknesses relative to that type." +
                              "If the speech is too short to evaluate a category, mark it as �insufficient data� instead of guessing." +
                              "If the words per minute are too high (significantly higher than 160) or too low (significantly lower than 140), mention it in the tips category";

        string userPrompt = 
        //(isInterview ? $@"The question asked was {question}" : "") +
            $@"
        Transcript: {transcription.Text}
        WPM: {words.Length / vidLength}
        Filler Count : {fillerCount}

        Return  a JSON with these keys:
        - 'strengths': (array of strings)
        - 'improvements': (array of strings)
        - 'tips': (array of strings)   
    ";


        ChatCompletionOptions options = new() { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() };
        ChatCompletion feedbackResult = await chat.CompleteChatAsync([new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)], options);
        var feedbackJson = feedbackResult.Content[0].Text;

        var feedbackObj = JsonSerializer.Deserialize<AiFeedback>(feedbackJson);


        return Results.Ok(new
        {
            Message = $"analysis complete : ",
            Transcript = string.IsNullOrWhiteSpace(transcription.Text) ? "Could not transcribe audio" : transcription.Text,
            metrics = new
            {
                totalWords = totalWords,
                wordsPerMinute = Math.Round(words.Length / vidLength, 1),
                fillerWords = fillerCount,
                pauseCount = longPauseCount
            },
            strengths = feedbackObj.strengths,
            improvements = feedbackObj.improvements,
            tips = feedbackObj.tips,

        }
        );
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error processing audio: {ex.Message}");
    }
    finally
    {
        if (File.Exists(videoPath))
            File.Delete(videoPath);
        if (File.Exists(audioPath))
            File.Delete(audioPath);
    }
}

async Task<double> GetVideoDuration(string filePath)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "ffprobe",
        Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(startInfo);
    string output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();

    return double.TryParse(output, out double seconds) ? seconds / 60.0 : 0.5; // Default to 30s if error
}
public record AiFeedback(
    List<string> strengths,
    List<string> improvements,
    List<string> tips
);
