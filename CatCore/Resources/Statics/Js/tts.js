
// { "voice": "Microsoft Huihui - Chinese (Simplified, PRC)", "rate": "1", "pitch": "1"}

class TTS {
	#synth = window.speechSynthesis;
	enable = false;
	pitch = 1;
	rate = 1;
	voices = null;
	voice = "";
	init = 0;

	constructor() {
		this.reload();
	}

	reload() {
		let conf_data = window.location.pathname === "/" ? data : config_data;
		this.enable = conf_data["overlay_tts_enable"];
		this.pitch = conf_data["overlay_tts_voice_pitch"] / 10;
		this.rate = conf_data["overlay_tts_voice_speed"] / 10;
		this.voices = new Promise(function (resolve, reject) {
			let voices = window.speechSynthesis.getVoices();
			if (voices.length !== 0) {
				resolve(voices);
			} else {
				window.speechSynthesis.addEventListener("voiceschanged", function () {
					voices = window.speechSynthesis.getVoices();
					resolve(voices);
				});
			}
		});
		this.voices.then(voices => {
			if (conf_data["overlay_tts_voice_package"] === "" && voices.length > 0) {
				this.voice = voices[0];
				this.init = 1;
			} else {
				for (let i = 0; i < voices.length; i++) {
					if (voices[i].name === conf_data["overlay_tts_voice_package"]) {
						this.voice = voices[i];
						this.init = 1;
						break;
					}
				}
			}
			
			if (this.init === 0) {
				this.enable = false;
				alert("Initialize TTS Engine failed, please check whether your OS/browser supports this function or disable TTS Engine by add query flag 'disableTTS=1' in !\n初始化TTS引擎失败，请检查系统是否支持或添加标志符'disableTTS=1'！");
			}
		});
	}

	selectVoiceByName(voiceName) {
		this.voices.then(voices => {
			for (let i = 0; i < voices.length; i++) {
				if (voices[i].name === voiceName) {
					this.voice = voices[i];
					break;
				}
			}
		});
	}

	selectVoiceByIndex(index) {
		this.voices.then(voices => {
			this.voice = voices[index];
		});
	}

	enable(enable) {
		this.enable = enable;
	}

	speak(text) {
		let textToSpeak = new SpeechSynthesisUtterance(text);
		textToSpeak.voice = this.voice;
		textToSpeak.pitch = this.pitch;
		textToSpeak.rate = this.rate;

		this.#synth.speak(textToSpeak);
	}

	test() {
		switch (userLang) {
			case "en":
				this.speak("ChatCore: This is a test! Hahahaha");
				break;
			case "zh":
				this.speak("ChatCore 说 这是测试，哈哈哈哈");
				break;
			case "ja":
				this.speak("ChatCore: これはテストです! ハハハッハッハ");
				break;
			default:
				this.speak("ChatCore: This is a test! Hahahaha");
				break;
		}
	}

	setAndTest(voice, pitch, rate) {
		this.pitch = pitch;
		this.rate = rate;
		this.voices.then(voices => {
			this.voice = voices[voice];
			test();
		});
	}

	cancelAll() {
		this.#synth.cancel();
	}
}
