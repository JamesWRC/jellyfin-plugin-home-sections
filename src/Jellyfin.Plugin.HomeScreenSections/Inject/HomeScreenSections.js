'use strict';

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

// Reveal-together gate (see PluginConfiguration.RevealSectionsTogether). Sections of a page are created hidden and each one
// appears when its own request returns, which moves everything below it. While gated, a page's sections are shown as
// fixed-height placeholders and are all released in the same frame once every one has finished, or after the timeout.
if (typeof HomeScreenSectionsGate == 'undefined') {
    const HomeScreenSectionsGate = {
        tracked: new WeakSet(),
        init: function () {
            var MutationObserver = window.MutationObserver || window.WebKitMutationObserver;
            new MutationObserver(function () { HomeScreenSectionsGate.scan(); })
                .observe(document.body, { childList: true, subtree: true });
        },
        scan: function () {
            var config = window.HssGateConfig;
            if (!config || !config.enabled) {
                return;
            }

            var pages = {};
            document.querySelectorAll('.homeSectionsContainer > .verticalSection[data-page]').forEach(function (section) {
                if (HomeScreenSectionsGate.tracked.has(section)) {
                    return;
                }

                HomeScreenSectionsGate.tracked.add(section);
                section.setAttribute('data-hss-gate', 'pending');
                (pages[section.getAttribute('data-page')] = pages[section.getAttribute('data-page')] || []).push(section);
            });

            Object.keys(pages).forEach(function (page) {
                HomeScreenSectionsGate.watch(pages[page], config.timeoutMs);
            });
        },
        watch: function (sections, timeoutMs) {
            var started = performance.now();

            (function check() {
                var finished = sections.every(function (section) {
                    return !section.isConnected || section.getAttribute('data-hss-fetched') === '1';
                });

                if (finished || performance.now() - started >= timeoutMs) {
                    // one frame later, so the cards of the last section have been drawn before anything is shown
                    requestAnimationFrame(function () {
                        sections.forEach(function (section) { section.removeAttribute('data-hss-gate'); });
                    });
                    return;
                }

                setTimeout(check, 40);
            })();
        }
    };

    $(document).ready(function () {
        HomeScreenSectionsGate.init();
    });
}
