// ==UserScript==
// @name         TikTok Upload Passthrough (EXPERIMENTAL)
// @namespace    local.optimizer
// @version      0.1
// @description  Strips TikTok's cloud-edit / canvas flags from the web publish request, the way the "Maska"/editingnews-class extensions do. EXPERIMENTAL — see notes below. Use at your own risk; this touches TikTok's upload and may violate TikTok's ToS.
// @match        https://www.tiktok.com/*
// @run-at       document-start
// @grant        none
// ==/UserScript==

/*
  WHAT THIS DOES (reverse-engineered from the free "Maska" add-on + a live capture
  of TikTok Studio's own publish request on 2026-11-14):

  TikTok Studio's publish call  ->  POST https://www.tiktok.com/tiktok/web/project/post/v1/
  Body (JSON) carries:  post_common_info{post_type,...}, single_post_req_list[0].single_post_feature_info{
      cloud_edit_is_use_video_canvas, cloud_edit_video_width/height, canvas_config?, vedit_segment_info?, ... }

  This script hooks fetch + XHR and, on that publish call, rewrites the body to the
  "no cloud-edit / old-web" shape:
     - post_type 2 -> 3            (route to the WITHOUT_CLOUD_EDIT pipeline)
     - cloud_edit_is_use_video_canvas -> false
     - delete canvas_config, vedit_segment_info, draft
  It edits only the JSON body (which TikTok does NOT sign), so no signature breaks.

  HONEST CAVEAT (from the live capture): on a NORMAL upload these flags are ALREADY
  in the clean state (cloud_edit_is_use_video_canvas was already false, no canvas_config),
  yet TikTok still re-encoded. The ONLY real change this makes is post_type 2 -> 3, whose
  effect on TikTok's SERVER-side transcode is UNPROVEN. There is NO "serve original" flag
  in the payload — quality is a server decision. So this may do nothing on its own.
  Treat it as an A/B experiment, not a guaranteed fix. Do NOT add a TikTok library sound
  in the test (that alone forces a re-encode).

  HOW TO TEST:
   1. Install in Tampermonkey/Violentmonkey. Upload a clean 1080p/1440p/4K clip, NO added sound.
   2. Watch the console for "[passthrough] rewrote publish payload".
   3. Post. Afterwards read the post's encodedType/videoQuality (original vs normal).
   4. Compare against the same clip posted WITHOUT the script.
*/

(function () {
  'use strict';
  const PUBLISH_RE = /\/(web\/project\/(post|create)|api\/v1\/item\/create|aweme\/v\d\/(aweme\/)?create)\b/i;

  function looksLikePost(o) {
    return o && typeof o === 'object' &&
      (o.single_post_req_list !== undefined || o.post_common_info !== undefined || o.vedit_common_info !== undefined);
  }
  function rewrite(node) {
    if (!node || typeof node !== 'object') return;
    for (const k of ['draft', 'canvas_config', 'vedit_segment_info']) if (k in node) delete node[k];
    if ('cloud_edit_is_use_video_canvas' in node) node.cloud_edit_is_use_video_canvas = false;
    if (node.post_type === 2) node.post_type = 3;
    for (const k in node) if (node[k] && typeof node[k] === 'object') rewrite(node[k]);
  }
  function maybeRewrite(url, body) {
    if (typeof body !== 'string' || !PUBLISH_RE.test(url || '')) return null;
    try { const o = JSON.parse(body); if (!looksLikePost(o)) return null; rewrite(o);
      console.log('[passthrough] rewrote publish payload ->', url); return JSON.stringify(o);
    } catch (e) { return null; }
  }

  const _fetch = window.fetch;
  window.fetch = function (res, opts) {
    try {
      const url = (typeof res === 'string') ? res : (res && res.url) || '';
      if (opts && opts.body) { const nb = maybeRewrite(url, opts.body); if (nb) opts = Object.assign({}, opts, { body: nb }); }
    } catch (e) {}
    return _fetch.apply(this, arguments);
  };

  const _open = XMLHttpRequest.prototype.open, _send = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (m, u) { this.__u = u; return _open.apply(this, arguments); };
  XMLHttpRequest.prototype.send = function (body) {
    try { const nb = maybeRewrite(this.__u, body); if (nb) { arguments[0] = nb; return _send.apply(this, arguments); } } catch (e) {}
    return _send.apply(this, arguments);
  };

  console.log('[passthrough] installed (experimental)');
})();
