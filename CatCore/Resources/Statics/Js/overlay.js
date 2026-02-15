const queryString = window.location.search;
const urlParams = new URLSearchParams(queryString);
const debug = urlParams.get('Debug');
const disableTTS = urlParams.has('disableTTS') && urlParams.get('disableTTS') === '1';

const proxy_url = `${window.location.origin}/?url=`;
const default_image_bilibili = `${window.location.origin}/Statics/Images/BilibiliDefaultAvatar.jpg`;
const default_image_twitch = `${window.location.origin}/Statics/Images/TwitchDefaultAvatar.png`;
const welcomePhases = [
	"这里是光剑弹幕姬哟~ －O－",
	"光剑弹幕姬帮你求舰长！ (。・∀・)ノ",
	"光剑弹幕姬不放过每一条弹幕 (ง •_•)ง",
	"不要小瞧光剑弹幕姬啊kora (ﾟДﾟ*)ﾉ",
	"让光剑弹幕姬来守护和谐的环境（￣︶￣）",
	"光剑弹幕姬出来了，很快啊 ( •̀ ω •́ )",
	"光剑弹幕姬，尊嘟假嘟 (/▽＼)",
	"光剑弹幕姬，遥遥领先 ╰(￣ω￣ｏ)"
];
const giftType = ["gift", "LIVE_OPEN_PLATFORM_SEND_GIFT", "combo_send", "combo_end"];
const normalDanmukuType = ["danmuku", "danmuku_motion", "LIVE_OPEN_PLATFORM_DM", "gift", "red_pocket", "combo_send", "combo_end", "welcome", "follow", "share", "special_follow", "mutual_follow", "welcome_guard", "new_guard", "new_guard_msg"];
const simplifiedDanmukuType = ["init", "join_channel", "gift_star", "effect", "global", "junk", "anchor_lot_checkstatus", "anchor_lot_start", "anchor_lot_end", "anchor_lot", "red_pocket_result", "raffle_start", "blacklist", "guard_msg", "guard_lottery_msg", "room_change", "room_preparing", "room_live", "warning", "cut_off", "pk_pre", "pk_start", "pk_end", "common_notice"];
const specialDanmukuType = ["gift_in_SC", "red_pocket_in_SC", "combo_send_in_SC", "combo_end_in_SC", "new_guard_in_SC", "super_chat", "super_chat_japanese", "LIVE_OPEN_PLATFORM_SUPER_CHAT", "LIVE_OPEN_PLATFORM_SUPER_CHAT_DEL"];

const showWelcomeMessage = setTimeout(function () {
	if (config_data["overlay_show_init_welcome"] && document.getElementById("overlay-msgs") != null) {
		clearInterval(showWelcomeMessage);
		formatBilibiliMessage({ MessageType: "init", Uid: 0, Sender: { UserName: "", Badges: [] }, UserName: "", Message: "", Conetent: "", extra: {} });
	}
}, 100);

var tts;


let messageQueue = [];
let messageDequeueInterval = 1500;
let messageDequeue_handler = setInterval(dequeueMessage, messageDequeueInterval);
let messageDequeue_delay = true;
let max_message_number = 25;
let message_index = 0;

class BilibiliMessage { }
class TwitchMessage { }

let formatTwitchMessage = function (data) {
	if (debug === "1") console.log(data);
	let twitchMessage = new TwitchMessage();
	twitchMessage.messageType = data["MessageType"];
	twitchMessage.message = data["Message"];
	twitchMessage.username = data["Sender"]["UserName"];
	twitchMessage.avatar = default_image_twitch;
	twitchMessage.tts_text = `${twitchMessage.username}: ${twitchMessage.message}`;
	enqueueMessage("twitch", twitchMessage.message, twitchMessage.messageType, bilibiliMsg.username, bilibiliMsg.avatar, 0.0, null, null, twitchMessage.tts_text);
}

let formatTwitchRawMessage = function (data) {
	// You can override this function
}

let formatBilibiliMessage = function (data) {
	if (debug === "1") console.log(data);
	let bilibiliMsg = new BilibiliMessage();
	bilibiliMsg.messageType = data["MessageType"];
	if ((config_data["overlay_show_gift_in_sc"] && (["gift", "red_pocket", "combo_end", "combo_send"].includes(bilibiliMsg.messageType)) || (config_data["overlay_show_guard_in_sc"] && ["new_guard", "new_guard_msg"].includes(bilibiliMsg.messageType)))) {
		bilibiliMsg.messageType += "_in_SC";
	}
	bilibiliMsg.uid = data["Uid"];
	console.log(data["Sender"]);
	bilibiliMsg.usernamePure = data["Sender"]["UserName"];
	bilibiliMsg.username = data["Username"];
	bilibiliMsg.avatar = default_image_bilibili;
	if (data["Sender"] != null) {
		if ((data["Sender"]["Badges"]).length > 0) {
			let uri = data["Sender"]["Badges"][(data["Sender"]["Badges"]).length - 1]["Uri"];
			if (uri !== undefined && typeof uri === "string" && uri !== null && uri.includes("/.chatcore/cache/Avatars/")) {
				bilibiliMsg.avatar = uri;
			}
		}
	}
	bilibiliMsg.message = data["Message"];
	bilibiliMsg.content = data["Content"];
	bilibiliMsg.extra = data["extra"];
	bilibiliMsg.price = 0.0;
	if (bilibiliMsg.messageType == "gift" || bilibiliMsg.messageType == "gift_in_SC") {
		bilibiliMsg.message = bilibiliMsg.content;
	}

	if (bilibiliMsg.messageType == "gift" ||
		bilibiliMsg.messageType == "gift_in_SC" ||
		bilibiliMsg.messageType == "red_pocket_start" ||
		bilibiliMsg.messageType == "red_pocket_new" ||
		bilibiliMsg.messageType == "new_guard" ||
		bilibiliMsg.messageType == "new_guard_msg"
	) {
		bilibiliMsg.price = data["extra"]["gift_price"];
	} else if (bilibiliMsg.messageType == "super_chat" ||
		bilibiliMsg.messageType == "super_chat_japanese"
	) {
		bilibiliMsg.price = data["extra"]["sc_price"]
	}
	bilibiliMsg.giftImg = "";
	if (bilibiliMsg.messageType == "gift" ||
		bilibiliMsg.messageType == "red_pocket_start" ||
		bilibiliMsg.messageType == "red_pocket_new" ||
		bilibiliMsg.messageType == "new_guard" ||
		bilibiliMsg.messageType == "new_guard_msg"
	) {
		bilibiliMsg.giftImg = data["extra"]["gift_img"];
	}
	if (bilibiliMsg.messageType == "danmuku_motion" || bilibiliMsg.messageType == "danmuku") {
		bilibiliMsg.extra["emotes"] = data["Emotes"];
	}

	// TTS
	switch (bilibiliMsg.messageType) {
		case "init":
			bilibiliMsg.message = welcomePhases[Math.floor(Math.random() * welcomePhases.length)];
			bilibiliMsg.tts_text = bilibiliMsg.message;
			break;
		case "danmuku":
			bilibiliMsg.tts_text = `${bilibiliMsg.usernamePure} 说 ${bilibiliMsg.message}`;
			break;
		case "danmuku_motion":
			bilibiliMsg.tts_text = `${bilibiliMsg.usernamePure} 发了一个 ${bilibiliMsg.message} 表情`;
			break;
		case "gift":
		case "gift_in_SC":
			bilibiliMsg.tts_text = `${bilibiliMsg.usernamePure}${bilibiliMsg.content}`;
			break;
		case "super_chat":
			bilibiliMsg.tts_text = `${bilibiliMsg.usernamePure} 的 ${(bilibiliMsg.extra)["sc_price"]} 元醒目留言说 ${bilibiliMsg.content}`;
			break;
		case "super_chat_japanese":
			bilibiliMsg.tts_text = `${bilibiliMsg.usernamePure} 的 ${(bilibiliMsg.extra)["sc_price"]} 日元醒目留言说 ${bilibiliMsg.content}`;
			break;
		case "gift_star":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "welcome":
		case "follow":
		case "share":
		case "special_follow":
		case "mutual_follow":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "welcome_guard":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "effect":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "anchor_lot_start":
		case "anchor_lot_checkstatus":
		case "anchor_lot_end":
		case "anchor_lot":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "red_pocket_start":
		case "red_pocket_new":
		case "red_pocket_result":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "RAFFLE_START":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "blocklist":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "new_guard":
		case "new_guard_msg":
		case "guard_msg":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "guard_lottery_msg":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "room_live":
		case "warning":
		case "cut_off":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "like_info":
			bilibiliMsg.tts_text = `${bilibiliMsg.usernamePure}${bilibiliMsg.message}`.replaceAll("[点赞图标]", "");
			break;
		case "pk_pre":
		case "pk_start":
		case "pk_end":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
			break;
		case "login_in_notice":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
		case "plugin_message":
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
		default:
			bilibiliMsg.tts_text = `${bilibiliMsg.message}`;
	}

	enqueueMessage("bilibili", bilibiliMsg.message, bilibiliMsg.messageType, bilibiliMsg.username, bilibiliMsg.avatar, bilibiliMsg.price, bilibiliMsg.giftImg, bilibiliMsg.extra, bilibiliMsg.tts_text);
}

let formatBilibiliRawMessage = function (data) {
	// You can override this function
}

let formatUnknownMessage = function (data) {
	// You can override this function
}

function debug_danmuku(command) {
	switch (command) {
		case 'danmuku':
		case '弹幕':
			enqueueMessage("bilibili", "测试弹幕~~~~~~~~~~~~~~~~~", "danmuku", "ChatCore", default_image_bilibili, undefined, undefined, undefined, "弹幕测试");
			break;
		case 'gift':
		case '礼物':
			if (config_data["overlay_show_gift_in_sc"]) {
				enqueueMessage("bilibili", "", "gift_in_SC", "ChatCore", default_image_bilibili, '0 (赠送1个羽毛球)', 'https://i0.hdslb.com/bfs/live/e3499facf6e1e2188d501c57f7ad4957140b9284.gif', undefined, "礼物测试，ChatCore赠送1个羽毛球");
			} else {
				enqueueMessage("bilibili", "赠送1个羽毛球", "gift", "ChatCore", default_image_bilibili, undefined, 'https://i0.hdslb.com/bfs/live/e3499facf6e1e2188d501c57f7ad4957140b9284.gif', undefined, "礼物测试，ChatCore赠送1个羽毛球");
			}
			break;
		case 'guard':
		case '大航海':
			enqueueMessage("bilibili", "现在没人加入舰队~", config_data["overlay_show_guard_in_sc"] ? "new_guard_in_SC" : "new_guard", "ChatCore", default_image_bilibili, '虚晃一枪！', "https://i0.hdslb.com/bfs/live/80f732943cc3367029df65e267960d56736a82ee.png", { id: '舰长', number: 1, type: "gold", img: "https://i0.hdslb.com/bfs/live/80f732943cc3367029df65e267960d56736a82ee.png" + "", gift_name: '舰长', price: '198' }, "大航海测试");
			break;
		case 'sc':
		case '醒目留言':
			enqueueMessage("bilibili", "这是一条假的醒目留言", "super_chat", "ChatCore", default_image_bilibili, "0.0", undefined, undefined, "醒目留言测试");
			break;
		case 'clearTTS':
		case '清空语音':
			if (urlParams.has('TTSConfig')) {
				tts.cancelAll();
				enqueueMessage("bilibili", "已清空语音队列", "danmuku", "ChatCore", default_image_bilibili, undefined, undefined, "已清空语音队列");
			}
	}
}

function enqueueMessage(channel_type, message, type, username, avatar, price, giftImg = undefined, extra = undefined, tts_text = undefined) {
	if (debug === "1") console.log(type, message, username, avatar, price, giftImg, extra, tts_text);
	let message_obj = {};
	message_obj['channel_type'] = channel_type;
	message_obj['message'] = message;
	message_obj['type'] = type;
	message_obj['username'] = username;
	message_obj['avatar'] = proxy_img(avatar);
	message_obj['price'] = price;
	message_obj['giftImg'] = proxy_img(giftImg);
	if (extra !== undefined && extra.hasOwnProperty("emotes")) {
		for (let i = 0; i < extra['emotes'].length; i++) {
			extra['emotes'][i]["Uri"] = proxy_img(extra['emotes'][i]["Uri"]);
		}
	}
	message_obj['extra'] = extra;
	messageQueue.push(message_obj);
	if (tts !== undefined && tts.enable && tts_text !== undefined && tts_text !== '') {
		tts.speak(tts_text);
	}
	if (!messageDequeue_delay) {
		dequeueMessage();
	}
}

function dequeueMessage(all = false) {
	if (all) {
		while (messageQueue.length > 0) {
			createMessage((messageQueue[0])['channel_type'], (messageQueue[0])['message'], (messageQueue[0])['type'], (messageQueue[0])['username'], (messageQueue[0])['avatar'], (messageQueue[0])['price'], (messageQueue[0])['giftImg'], (messageQueue[0])['extra']);
			messageQueue.shift();
		}
	} else if (messageQueue.length > 0) {
		createMessage((messageQueue[0])['channel_type'], (messageQueue[0])['message'], (messageQueue[0])['type'], (messageQueue[0])['username'], (messageQueue[0])['avatar'], (messageQueue[0])['price'], (messageQueue[0])['giftImg'], (messageQueue[0])['extra']);
		messageQueue.shift();
	}
}

async function reloadConfig() {
	if (tts !== undefined) {
		tts.reload();
	}
}

function linkWebsocket() {
	let port = parseInt(window.location.port);
	port = port === 65535 ? 1 : port + 1;
	let _websocket = new WebSocket(`ws://${window.location.hostname}:${port}`);
	let _websocketHeartBeat = setInterval(function () { }, 300000);

	function heartBeat() {
		_websocket.send(JSON.stringify({ cmd: "ping" }));
		_websocketHeartBeat = setTimeout(heartBeat, 5000);
	}

	_websocket.onopen = function () {
		_websocket.send(JSON.stringify({
			cmd: "sub",
			channels: [
				"bilibili",
				"twitch"
			],
		}));
		_websocketHeartBeat = setTimeout(heartBeat, 5000);
	}

	_websocket.onmessage = function (msgEvent) {
		let packet = JSON.parse(msgEvent.data);
		switch (packet.cmd) {
			case "pong":
				break;
			case "sub":
			case "unsub":
				break;
			case "disconnected":
				break;
			case "data":
				switch (packet["channel"]) {
					case "twitch":
						formatTwitchMessage(JSON.parse(packet["data"]));
						break;
					case "twitch_raw":
						formatTwitchRawMessage(JSON.parse(packet["data"]));
						break;
					case "bilibili":
						console.log(packet);
						formatBilibiliMessage(JSON.parse(packet["data"]));
						break;
					case "bilibili_raw":
						formatBilibiliRawMessage(JSON.parse(packet["data"]));
						break;
					default:
						console.log(packet);
						formatUnknownMessage(JSON.parse(packet["data"]));
				}
				break;
		}
	};

	_websocket.onclose = function () {
		console.log("弹幕姬与ChatCore已断开");
		clearInterval(_websocketHeartBeat);
		setTimeout(function () {
			linkWebsocket();
		}, 1000);
	}

	_websocket.onerror = function () {
		console.log("弹幕姬与ChatCore连接出现错误");
		clearInterval(_websocketHeartBeat);
		_websocket.close();
	}
}

window.onload = async () => {
	if (!disableTTS && config_data.hasOwnProperty('overlay_tts_enable') && config_data["overlay_tts_enable"]) {
		tts = new TTS();
	}
	await reloadConfig();
	linkWebsocket();
}

function proxy_img(url) {
	if (url == undefined || url.startsWith(window.location.origin)) {
		return url;
	} else {
		return proxy_url + url;
	}
}
var createMessage = function(channel_type, message, type, username, avatar, price, giftImg = undefined, extra = undefined) {
	if (debug === "1") console.log("createDanmuku", type, message, username, avatar, price, giftImg, extra);
	if (channel_type === "twitch" || normalDanmukuType.includes(type)) {
		let messageElement = document.createElement("div");
		let messageAvatarElement = document.createElement("div");
		let messageUsernameElement = document.createElement("div");
		let messageContentElement = document.createElement("div");
		let messageBubbleElement = document.createElement("div");

		messageElement.className = "msg-danmuku-msg msg-danmuku-normalMsg";
		messageElement.id = "message_index_" + message_index;
		message_index++;
		messageAvatarElement.className = "msg-danmuku-avatar";
		messageAvatarElement.setAttribute("style", `background-image: url(${avatar})`);
		messageUsernameElement.className = "msg-danmuku-username";
		messageUsernameElement.textContent = (type === 'danmuku') ? (config_data["overlay_show_username"] ? username : "") : username;
		messageContentElement.className = "msg-danmuku-content-bubble";
		let replaced_message = message;
		if (extra !== undefined && extra.hasOwnProperty("emotes")) {
			extra["emotes"].forEach((emotes) => {
				console.log(`Replace ${emotes["Name"]} ==> <img src="${emotes["Uri"]}"></img>`);
				if (emotes["Id"].startsWith("official_")) {
					extra.width = 200;
					extra.height = 100;
				}
				replaced_message = replaced_message.substring(0, emotes["StartIndex"]) + replaced_message.substring(emotes["StartIndex"], emotes["EndIndex"]).replaceAll(emotes["Name"], `<img src="${emotes["Uri"]}"></img>`) + replaced_message.substring(emotes["EndIndex"]);
				console.log(replaced_message);
			});
			messageContentElement.innerHTML = replaced_message;
		} else {
			messageContentElement.textContent = (type === "danmuku_motion") ? "" : message;
		}
		messageBubbleElement.className = "msg-bubble";
		messageElement.appendChild(messageAvatarElement);
		messageBubbleElement.appendChild(messageUsernameElement);
		if (giftType.includes(type)) {
			let danmukuGiftContentElement = document.createElement("div");
			let danmukuGiftImgElement = document.createElement("div");
			danmukuGiftContentElement.className = "msg-danmuku-gift-content";
			danmukuGiftImgElement.setAttribute("style", `background-image: url(${giftImg})`);
			danmukuGiftImgElement.className = "msg-danmuku-giftImg";
			danmukuGiftContentElement.appendChild(messageContentElement);
			danmukuGiftContentElement.appendChild(danmukuGiftImgElement);
			messageBubbleElement.appendChild(danmukuGiftContentElement);
			messageElement.appendChild(messageBubbleElement);
		} else if (type === "danmuku_motion") {
			let danmukuMotionImgElement = document.createElement("div");
			danmukuMotionImgElement.innerHTML = replaced_message;
			danmukuMotionImgElement.className = extra.width === extra.height ? "msg-danmuku-motionImg-proprietary" : "msg-danmuku-motionImg";
			messageElement.appendChild(danmukuMotionImgElement);
		} else {
			messageBubbleElement.appendChild(messageContentElement);
			messageElement.appendChild(messageBubbleElement);
		}
		document.getElementById("overlay-msgs").appendChild(messageElement);
		animateNewMsg(messageElement.id);
	} else if (simplifiedDanmukuType.includes(type)) {
		if (type === "effect") {
			let danmukuElement = document.createElement("div");
			let danmukuAvatarElement = document.createElement("div");
			let danmukuContentElement = document.createElement("div");
			let danmukuBubbleElement = document.createElement("div");
			danmukuElement.className = "msg-danmuku-msg msg-danmuku-SimplifiedMsg-withAvatar";
			danmukuElement.id = "message_index_" + message_index;
			message_index++;
			danmukuAvatarElement.className = "msg-danmuku-avatar";
			danmukuAvatarElement.setAttribute("style", `background-image: url(${avatar})`);
			danmukuContentElement.className = "msg-danmuku-content";
			danmukuContentElement.textContent = message;
			danmukuBubbleElement.className = "msg-bubble";
			danmukuElement.appendChild(danmukuAvatarElement);
			danmukuBubbleElement.appendChild(danmukuContentElement);
			danmukuElement.appendChild(danmukuBubbleElement);
			document.getElementById("overlay-msgs").appendChild(danmukuElement);
			animateNewMsg(danmukuElement.id);
		} else {
			let danmukuElement = document.createElement("div");
			let danmukuContentElement = document.createElement("div");
			danmukuElement.className = "msg-danmuku-msg msg-danmuku-simplifiedMsg";
			danmukuElement.id = "message_index_" + message_index;
			message_index++;
			danmukuContentElement.className = "msg-danmuku-content";
			danmukuContentElement.textContent = message;
			danmukuElement.appendChild(danmukuContentElement);
			document.getElementById("overlay-msgs").appendChild(danmukuElement);
			animateNewMsg(danmukuElement.id);
		}
	} else if (specialDanmukuType.includes(type)) {
		let danmukuSuperChatElement = document.createElement("div");
		let danmukuSuperChatTop = document.createElement("div");
		let danmukuSuperChatTop2 = document.createElement("div");
		let danmukuAvatarElement = document.createElement("div");
		let danmukuUsernameElement = document.createElement("div");
		let danmukuPriceElement = document.createElement("div");
		let danmukuContentElement = document.createElement("div");
		danmukuSuperChatElement.className = "msg-danmuku-msg msg-danmuku-SuperChatMsg";
		danmukuSuperChatElement.id = "message_index_" + message_index;
		message_index++;
		danmukuSuperChatTop.className = "msg-danmuku-SuperChatTop";
		danmukuSuperChatTop2.className = "msg-danmuku-SuperChatTop2";
		danmukuAvatarElement.className = "msg-SuperChat-avatar";
		danmukuAvatarElement.setAttribute("style", `background-image: url(${avatar})`);
		danmukuUsernameElement.className = "msg-SuperChat-username";
		danmukuUsernameElement.innerHTML = config_data["overlay_show_username"] ? username : "";
		danmukuContentElement.className = "msg-SuperChat-content";
		danmukuContentElement.textContent = message;
		danmukuPriceElement.className = "msg-SuperChat-price";
		danmukuPriceElement.textContent = `${extra["gift_type"] === "silver" ? "免" : ""}${type === "new_guard_in_SC" ? "" : "￥"}${price}`;
		danmukuSuperChatTop.appendChild(danmukuAvatarElement);
		danmukuSuperChatTop2.appendChild(danmukuUsernameElement);
		danmukuSuperChatTop2.appendChild(danmukuPriceElement);
		danmukuSuperChatTop.appendChild(danmukuSuperChatTop2);
		danmukuSuperChatElement.appendChild(danmukuSuperChatTop);
		danmukuSuperChatElement.appendChild(danmukuContentElement);
		document.getElementById("overlay-msgs").appendChild(danmukuSuperChatElement);
		animateNewMsg(danmukuSuperChatElement.id);
	}
	let msgList = document.getElementById("overlay-msgs");
	if (msgList.childNodes.length > max_message_number) msgList.removeChild(msgList.childNodes[0]);
}

function animateNewMsg(msg_id) {
	anime({
		targets: '#' + msg_id,
		scale: {
			value: [0.001, 1],
			duration: 800
		},
		translateX: {
			value: [$().width(), 0],
			delay: 0,
			duration: 800,
			easing: 'easeInQuad'
		},
		delay: 500
	});
	anime({
		targets: '.msg-danmuku-msg:not(#' + msg_id + ')',
		translateY: {
			value: [$('#' + msg_id).height(), 0],
			delay: 0,
			duration: 500,
			easing: 'easeInQuad'
		}
	});
}