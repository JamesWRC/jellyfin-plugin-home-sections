'use strict';

// Boot outline (PluginConfiguration.ShowBootSkeleton). This file is loaded in the first few hundred milliseconds, long before the
// web app has started and created the first section, so it can paint an outline of the home page straight away. The option is
// read from localStorage (written by the section loader when it reads /HomeScreen/Meta), so it applies from a user's second
// visit. The outline is removed as soon as the real section placeholders exist, if the user does not use these sections, when
// the user leaves the home page, or after 8 seconds at the latest.
(function () {
    try {
        var hash = window.location.hash;
        if (localStorage.getItem('hss.bootSkeleton') !== '1' || (hash !== '' && hash !== '#' && hash.indexOf('#/home') !== 0)) {
            return;
        }

        var root = document.documentElement;
        var observer = null;
        var off = function () {
            root.classList.remove('hss-boot');
            if (observer) { observer.disconnect(); observer = null; }
        };

        window.HssBootSkeletonOff = off;
        root.classList.add('hss-boot');
        requestAnimationFrame(function () { performance.mark('hss-boot-painted'); });

        var queued = false;
        observer = new MutationObserver(function () {
            if (queued) { return; }
            queued = true;
            requestAnimationFrame(function () {
                queued = false;
                if (document.querySelector('.homeSectionsContainer > .verticalSection')) {
                    off();
                }
            });
        });
        observer.observe(root, { childList: true, subtree: true });
        window.addEventListener('hashchange', function () { if (window.location.hash.indexOf('#/home') !== 0) { off(); } });
        setTimeout(off, 8000);
    } catch (e) { /* the outline is cosmetic */ }
})();

if (typeof HomeScreenSectionsHandler == 'undefined') {
    const HomeScreenSectionsHandler = {
        init: function() {
            var MutationObserver = window.MutationObserver || window.WebKitMutationObserver;
            var myObserver = new MutationObserver(this.mutationHandler);
            var observerConfig = {childList: true, characterData: true, attributes: true, subtree: true};

            $("body").each(function () {
                myObserver.observe(this, observerConfig);
            });
        },
        mutationHandler: function (mutationRecords) {
            mutationRecords.forEach(function (mutation) {
                if (mutation.addedNodes && mutation.addedNodes.length > 0) {
                    [].some.call(mutation.addedNodes, function (addedNode) {
                        if ($(addedNode).hasClass('discover-card')) {
                            $(addedNode).on('click', '.discover-requestbutton', HomeScreenSectionsHandler.clickHandler);
                        }
                    });
                }
            });
        },
        clickHandler: function(event) {
            window.ApiClient.ajax({
                url: window.ApiClient.getUrl("HomeScreen/DiscoverRequest"),
                type: "POST",
                data: JSON.stringify({
                    UserId: window.ApiClient._currentUser.Id,
                    MediaType: $(this).data('media-type'),
                    MediaId: $(this).data('id'),
                }),
                contentType: 'application/json; charset=utf-8',
                dataType: 'json'
            }).then(function(response) {
                if (response.errors && response.errors.length > 0) {
                    Dashboard.alert("Item request failed. Check browser logs for details.");
                    console.error("Item request failed. Response including errors:");
                    console.error(response);
                } else {
                    Dashboard.alert("Item successfully requested");
                }
            }, function(error) {
                Dashboard.alert("Item request failed");
            })
        }
    };
    
    $(document).ready(function () {
        setTimeout(function () {
            HomeScreenSectionsHandler.init();
        }, 50);
    });
}

if (typeof TopTenSectionHandler == 'undefined') {
    const TopTenSectionHandler = {
        init: function () {
            var MutationObserver = window.MutationObserver || window.WebKitMutationObserver;
            var myObserver = new MutationObserver(this.mutationHandler);
            var observerConfig = {childList: true, characterData: true, attributes: true, subtree: true};

            $("body").each(function () {
                myObserver.observe(this, observerConfig);
            });
        },
        mutationHandler: function (mutationRecords) {
            mutationRecords.forEach(function (mutation) {
                if (mutation.addedNodes && mutation.addedNodes.length > 0) {
                    [].some.call(mutation.addedNodes, function (addedNode) {
                        if ($(addedNode).hasClass('card')) {
                            if ($(addedNode).parents('.top-ten').length > 0) {
                                var index = parseInt($(addedNode).attr('data-index'));
                                $(addedNode).attr('data-number', index + 1);
                            }
                        }
                    });
                }
            });
        }
    }

    setTimeout(function () {
        TopTenSectionHandler.init();
    }, 50);
}

// Reveal-together gate (PluginConfiguration.RevealSectionsTogether) and image preloading (PluginConfiguration.PreloadSectionImages).
//
// Gate: sections of a page are created hidden and each one appears when its own request returns, which moves everything below
// it. While gated, a page's sections are shown as fixed-height placeholders and are all released in the same frame once every
// one has finished, or after the timeout.
//
// Preload: Jellyfin loads a card's image only when the card is within half a screen of the viewport, then fades it in over the
// blurhash. Scrolling a row therefore shows each new card blurred or empty for a moment. With preloading, the images of the
// cards in the first screen are decoded before the sections are revealed, and every other card in the rows is loaded in the
// background and put in place finished (no blurhash, no fade) so there is nothing left to load when it is scrolled into view.
if (typeof HomeScreenSectionsGate == 'undefined') {
    const HomeScreenSectionsGate = {
        tracked: new WeakSet(),
        queue: [],
        active: 0,
        maxActive: 6,
        scanQueued: false,
        pending: new Set(),
        releasedOnce: false,
        init: function () {
            var MutationObserver = window.MutationObserver || window.WebKitMutationObserver;
            new MutationObserver(function () {
                if (!HomeScreenSectionsGate.scanQueued) {
                    HomeScreenSectionsGate.scanQueued = true;
                    requestAnimationFrame(function () {
                        HomeScreenSectionsGate.scanQueued = false;
                        HomeScreenSectionsGate.scan();
                    });
                }
            }).observe(document.body, { childList: true, subtree: true });

            // Safety net. A section held by the gate must never stay hidden: the page can wake up late (a phone's web view
            // pauses timers and animation frames while it is in the background or mid-transition and runs them again on
            // the first touch), so anything held for too long is released, on a timer and on every sign of life.
            var wake = function () { HomeScreenSectionsGate.releaseStale(1500); };
            setInterval(function () { HomeScreenSectionsGate.releaseStale(2500); }, 500);
            ['touchstart', 'touchmove', 'scroll', 'focus', 'pageshow', 'visibilitychange', 'resize', 'hashchange'].forEach(function (name) {
                window.addEventListener(name, wake, { passive: true });
            });
            document.addEventListener('visibilitychange', wake);
        },
        releaseStale: function (maxAgeMs) {
            var gate = HomeScreenSectionsGate;
            var now = performance.now();
            gate.pending.forEach(function (section) {
                if (!section.isConnected || now - section.__hssSince > maxAgeMs) {
                    section.removeAttribute('data-hss-gate');
                    gate.pending.delete(section);
                }
            });
        },
        scan: function () {
            var config = window.HssGateConfig;
            if (!config || !document.querySelector('.homeSectionsContainer')) {
                return;
            }

            if (config.enabled) {
                var pages = {};
                document.querySelectorAll('.homeSectionsContainer > .verticalSection[data-page]').forEach(function (section) {
                    if (HomeScreenSectionsGate.tracked.has(section)) {
                        return;
                    }

                    HomeScreenSectionsGate.tracked.add(section);
                    section.setAttribute('data-hss-gate', 'pending');
                    section.__hssSince = performance.now();
                    HomeScreenSectionsGate.pending.add(section);
                    (pages[section.getAttribute('data-page')] = pages[section.getAttribute('data-page')] || []).push(section);
                });

                Object.keys(pages).forEach(function (page) {
                    HomeScreenSectionsGate.watch(pages[page], config.timeoutMs, config.preloadImages);
                });
            } else if (config.preloadImages) {
                // no gate: every card that is not loaded yet goes to the background queue, in page order
                HomeScreenSectionsGate.enqueue(HomeScreenSectionsGate.cardsIn(document.querySelectorAll('.homeSectionsContainer > .verticalSection')));
            }
        },
        cardsIn: function (sections) {
            var cards = [];
            Array.prototype.forEach.call(sections, function (section) {
                Array.prototype.forEach.call(section.querySelectorAll('.cardImageContainer[data-src]'), function (card) { cards.push(card); });
            });
            return cards;
        },
        // true for a card that will be on screen when the page is first shown
        isFirstScreen: function (card) {
            var rect = card.getBoundingClientRect();
            return rect.width > 0 && rect.top < window.innerHeight * 1.1 && rect.left < window.innerWidth * 1.1 && rect.bottom > 0;
        },
        // Fetch and decode the image, then put it on the card finished: no fade, no blurhash behind it.
        hydrate: function (card) {
            var url = card.getAttribute('data-src');
            if (!url || !card.isConnected) {
                return Promise.resolve(); // nothing to load, or the home page was rebuilt and this card is gone
            }

            var image = new Image();
            image.decoding = 'async';
            image.src = url;

            var finish = function () {
                if (!card.isConnected || card.getAttribute('data-src') !== url) {
                    return; // the lazy loader got there first
                }

                card.style.backgroundImage = "url('" + url.replace(/[\\'\n\r\f]/g, function (c) { return '\\' + c.charCodeAt(0).toString(16) + ' '; }) + "')";
                card.removeAttribute('data-src');
                card.classList.remove('lazy-hidden');

                var canvas = card.previousSibling;
                if (canvas && canvas.tagName === 'CANVAS') {
                    canvas.classList.add('lazy-hidden');
                }

                var padder = card.parentNode && card.parentNode.querySelector('.cardPadder');
                if (padder) {
                    padder.classList.add('lazy-hidden-children');
                }
            };

            var loaded = image.decode ? image.decode() : new Promise(function (resolve, reject) { image.onload = resolve; image.onerror = reject; });
            // a decode that never settles must not keep one of the few download slots forever
            var guarded = Promise.race([loaded, new Promise(function (resolve) { setTimeout(resolve, 8000); })]);
            return guarded.then(finish, function () { /* leave this card to the lazy loader */ });
        },
        enqueue: function (cards) {
            cards.forEach(function (card) {
                if (card.getAttribute('data-src') && !card.__hssQueued) {
                    card.__hssQueued = true;
                    HomeScreenSectionsGate.queue.push(card);
                }
            });
            HomeScreenSectionsGate.pump();
        },
        pump: function () {
            var gate = HomeScreenSectionsGate;
            while (gate.active < gate.maxActive && gate.queue.length > 0) {
                var card = gate.queue.shift();
                gate.active++;
                gate.hydrate(card).then(function () {
                    gate.active--;
                    gate.pump();
                });
            }
        },
        watch: function (sections, timeoutMs, preloadImages) {
            var started = performance.now();
            // Only the first home page of a session is held for all its rows. Coming back to the home page (the sections are
            // rebuilt, but the server has them cached) shows them as they arrive, after a short wait at most.
            var isReturn = HomeScreenSectionsGate.releasedOnce;
            var waitImages = preloadImages && !isReturn;
            if (isReturn) { timeoutMs = Math.min(timeoutMs, 400); }

            (function check() {
                var finished = sections.every(function (section) {
                    return !section.isConnected || section.getAttribute('data-hss-fetched') === '1';
                });
                var timedOut = performance.now() - started >= timeoutMs;

                if (!finished && !timedOut) {
                    setTimeout(check, 40);
                    return;
                }

                var release = function () {
                    var done = false;
                    var show = function () {
                        if (done) { return; }
                        done = true;
                        sections.forEach(function (section) { section.removeAttribute('data-hss-gate'); HomeScreenSectionsGate.pending.delete(section); });
                        HomeScreenSectionsGate.releasedOnce = true;
                        if (preloadImages) {
                            HomeScreenSectionsGate.enqueue(HomeScreenSectionsGate.cardsIn(sections));
                        }
                    };

                    // one frame later, so the cards of the last section have been drawn before anything is shown;
                    // the timer covers a background tab, where animation frames do not run
                    requestAnimationFrame(show);
                    setTimeout(show, 60);
                };

                if (!waitImages) {
                    release();
                    return;
                }

                // Hold the reveal until the first screen's images are decoded, but never for long.
                var cards = HomeScreenSectionsGate.cardsIn(sections);
                var firstScreen = cards.filter(HomeScreenSectionsGate.isFirstScreen);
                var grace = Math.max(timeoutMs - (performance.now() - started), 0) + 400;
                Promise.race([
                    Promise.all(firstScreen.map(HomeScreenSectionsGate.hydrate)),
                    new Promise(function (resolve) { setTimeout(resolve, grace); })
                ]).then(release);
            })();
        }
    };

    $(document).ready(function () {
        HomeScreenSectionsGate.init();
    });
}

// Streaming rows: a title the server does not have opens through Gelato (which adds it and streams it), where the
// server allows this user; otherwise it falls back to the request page. Library titles never reach this handler.
(function () {
    if (window.__hssOpenCard) { return; }
    window.__hssOpenCard = true;
    window.addEventListener('click', function (ev) {
        var card = ev.target && ev.target.closest ? ev.target.closest('.hss-open-card') : null;
        if (!card || ev.target.closest('.discover-requestbutton')) { return; }
        if (ev.button !== 0 || ev.ctrlKey || ev.metaKey || ev.shiftKey) { return; }
        ev.preventDefault();
        ev.stopPropagation();
        ev.stopImmediatePropagation();
        if (card.getAttribute('data-hss-busy')) { return; }
        var link = card.querySelector('a[href]');
        var fallback = link ? link.href : null;
        var type = card.getAttribute('data-media-type') === 'tv' ? 'tv' : 'movie';
        var id = card.getAttribute('data-tmdb-id');
        var done = function () { card.style.opacity = ''; card.removeAttribute('data-hss-busy'); };
        var fall = function () { done(); if (fallback) { window.open(fallback, '_blank', 'noopener'); } };
        card.setAttribute('data-hss-busy', '1');
        card.style.opacity = '.6';
        window.ApiClient.ajax({ url: window.ApiClient.getUrl('gelato/open/' + type + '/' + encodeURIComponent(id)), type: 'POST', dataType: 'json' })
            .then(function (r) {
                if (r && r.Id) {
                    done();
                    window.location.hash = '#/details?id=' + r.Id + '&serverId=' + window.ApiClient.serverId();
                } else { fall(); }
            }, fall);
    }, true);
})();
