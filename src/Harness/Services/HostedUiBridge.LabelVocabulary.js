const harnessLabelVocabulary = (() => {
  // Latin terms match on word boundaries so "stopped" does not match "stop" in a
  // different sense, and CJK terms match as substrings because \b is not a word
  // boundary between CJK codepoints -- /\bstop\b/ style patterns never fire on a
  // Chinese or Japanese Control UI.
  const buildMatcher = (latinTerms, cjkTerms) => {
    const latinPattern = latinTerms.length > 0
      ? new RegExp(`\\b(?:${latinTerms.join('|')})\\b`, 'i')
      : null;

    return (value) => {
      const text = (value == null ? '' : String(value)).toLowerCase();
      if (!text) return false;
      if (latinPattern && latinPattern.test(text)) return true;
      return cjkTerms.some((term) => text.includes(term));
    };
  };

  const STOP_LATIN = ['stop', 'abort', 'cancel', 'halt', 'interrupt', 'terminate', 'pause'];
  const STOP_CJK = [
    '停止', '中止', '取消', '中断', '終止', '终止', '暂停', '暫停',
    '스톱', '중지', '취소'
  ];

  const SHELL_LATIN = [
    'stop', 'abort', 'dashboard', 'settings', 'sessions', 'workers', 'models',
    'new chat', 'history', 'send', 'submit', 'conversation', 'chat'
  ];
  const SHELL_CJK = [
    '停止', '中止', '仪表板', '儀表板', '设置', '設定', '设定', '会话', '會話',
    '模型', '新对话', '新對話', '新建对话', '新聊天', '历史', '歷史', '记录', '記錄',
    '发送', '發送', '提交', '对话', '對話', '聊天', '工作区', '工作區',
    '設定', 'セッション', '送信', '履歴', '새 채팅', '전송', '설정'
  ];

  const AUTH_LATIN = [
    'authentication required', 'authorization failed', 'unauthorized',
    'access denied', 'token required', 'password required',
    'session expired', 'sign in', 'log in', 'login required', 'please log in'
  ];
  const AUTH_CJK = [
    '需要认证', '需要認證', '需要身份验证', '需要身份驗證', '认证失败', '認證失敗',
    '授权失败', '授權失敗', '未授权', '未授權', '访问被拒绝', '訪問被拒絕',
    '拒绝访问', '需要令牌', '需要密码', '需要密碼', '会话已过期', '會話已過期',
    '登录已过期', '登錄已過期', '请登录', '請登錄', '请先登录', '登录', '登錄', '登入',
    'ログインが必要', 'セッションの有効期限', '로그인'
  ];

  const GATEWAY_ERROR_LATIN = [
    'unable to connect', 'connection lost', 'gateway unavailable',
    'failed to connect', 'websocket closed', 'disconnect code',
    'network error', 'server error'
  ];
  const GATEWAY_ERROR_CJK = [
    '无法连接', '無法連接', '连接丢失', '連接丟失', '连接断开', '連接斷開',
    '连接失败', '連接失敗', '网关不可用', '網關不可用', '服务不可用', '服務不可用',
    '网络错误', '網絡錯誤', '服务器错误', '伺服器錯誤', '已断开', '已斷開',
    '接続できません', '接続が失われました', '연결 실패', '연결이 끊'
  ];

  const CONNECTING_LATIN = [
    'connecting to gateway', 'waiting for gateway',
    'reconnecting', 'establishing connection', 'connecting'
  ];
  const CONNECTING_CJK = [
    '正在连接', '正在連接', '连接中', '連接中', '重新连接', '重新連接',
    '正在重连', '正在重連', '等待网关', '等待網關', '建立连接', '建立連接',
    '接続中', '再接続', '연결 중'
  ];

  const PAIRING_LATIN = [
    'pairing required', 'pair this device', 'device approval required',
    'device not paired', 'disconnected (1008)'
  ];
  const PAIRING_CJK = [
    '需要配对', '需要配對', '配对此设备', '配對此裝置', '需要设备批准', '需要裝置批准',
    '设备未配对', '裝置未配對', '等待批准', '等待審批'
  ];

  const RATE_LIMIT_LATIN = [
    'retry later', 'too many failed auth attempts', 'retry-after',
    'rate limited', 'rate limit', 'too many requests'
  ];
  const RATE_LIMIT_CJK = [
    '请稍后重试', '請稍後重試', '稍后再试', '稍後再試', '请求过于频繁', '請求過於頻繁',
    '限流', '频率限制', '頻率限制', '尝试次数过多', '嘗試次數過多'
  ];

  const BUSY_LATIN = [
    'generating', 'streaming', 'thinking', 'running', 'working',
    'processing', 'loading', 'in progress'
  ];
  const BUSY_CJK = [
    '生成中', '正在生成', '思考中', '正在思考', '运行中', '運行中',
    '正在运行', '正在執行', '执行中', '執行中', '处理中', '處理中',
    '加载中', '載入中', '正在加载', '回复中', '回覆中', '输出中', '輸出中',
    '生成しています', '実行中', '処理中', '생성 중', '실행 중'
  ];

  return {
    buildMatcher,
    matchesStop: buildMatcher(STOP_LATIN, STOP_CJK),
    matchesShellAffordance: buildMatcher(SHELL_LATIN, SHELL_CJK),
    matchesAuth: buildMatcher(AUTH_LATIN, AUTH_CJK),
    matchesGatewayError: buildMatcher(GATEWAY_ERROR_LATIN, GATEWAY_ERROR_CJK),
    matchesConnecting: buildMatcher(CONNECTING_LATIN, CONNECTING_CJK),
    matchesPairing: buildMatcher(PAIRING_LATIN, PAIRING_CJK),
    matchesRateLimit: buildMatcher(RATE_LIMIT_LATIN, RATE_LIMIT_CJK),
    matchesBusy: buildMatcher(BUSY_LATIN, BUSY_CJK)
  };
})();
