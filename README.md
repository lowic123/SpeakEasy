**SpeakEasy**

AI-Powered Speech & Interview Feedback Platform

SpeakEasy is a full-stack web application that analyzes recorded speech and provides structured, AI-generated feedback.

Users upload a video, and the system automatically extracts audio, transcribes speech, computes metrics (WPM, filler words), and generates qualitative general tips using LLMs.

*Built in 24 hours during a hackathon*

**Key Features**

- Video upload & automated processing

- Speech-to-text transcription

- Words per minute calculation

- Filler word detection

- AI-generated strengths, weaknesses & improvement tips

- Behavioral & technical interview simulation modes

**Architecture Overview**


Frontend (HTML/CSS/JS)
        →
REST API (.NET / C#)
        →
FFmpeg (Audio Extraction)
        →
Whisper-1 (Transcription)
        →
Metric Processing (WPM, Filler Words)
        →
GPT-4o mini (Structured Feedback)
        →
JSON Response → UI Rendering

**Tech Stack**

**Frontend**

- HTML, CSS, JavaScript


**Backend**

- C# (.NET)

- RESTful endpoints


**AI & Media Processing**

- FFmpeg

- Whisper-1

- GPT-4o mini

**Interview Modes**

Behavioral Mode

1. AI generates a behavioral interview question

2. User uploads video response

3. System evaluates structure, clarity, and communication effectiveness

Technical Mode

1. AI generates a technical interview question
   
2. AI evaluates explanation depth and logical flow

Setup requirements

- .NET SDK

- FFmpeg installed and added to PATH

- OpenAI API key as environment variable
