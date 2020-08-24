using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Models;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Zammad.Client;
using Zammad.Client.Resources;

namespace BTCPayServer.Zammad
{
    public class ZammadController : Controller
    {
        private readonly SettingsRepository _settingsRepository;
        private readonly UserManager<ApplicationUser> _userManager;

        public ZammadController(SettingsRepository settingsRepository, UserManager<ApplicationUser> userManager)
        {
            _settingsRepository = settingsRepository;
            _userManager = userManager;
        }

        [HttpGet("~/server/zammad")]
        [Authorize(Policy = BTCPayServer.Client.Policies.CanModifyServerSettings,
            AuthenticationSchemes = BTCPayServer.Security.AuthenticationSchemes.Cookie)]
        public async Task<IActionResult> UpdateSetting()
        {
            var settings = await _settingsRepository.GetSettingAsync<ZammadOptions>();
            var vm = new ZammadOptionsVM() { };
            vm.FromOptions(settings);
            try
            {
                if (vm.Configured is true)
                {
                    var client = ZammadAccount.CreateTokenAccount(vm.Endpoint, vm.APIKey);
                    vm.Groups = new SelectList((await client.CreateGroupClient().GetGroupListAsync()).Select(group =>
                            new SelectListItem(group.Name, group.Id.ToString())), nameof(SelectListItem.Value),
                        nameof(SelectListItem.Text));
                }
            }
            catch (Exception e)
            {
            }

            return View(vm);
        }

        [HttpPost("~/server/zammad")]
        public async Task<IActionResult> UpdateSetting(ZammadOptionsVM setting)
        {
            try
            {
                var client = ZammadAccount.CreateTokenAccount(setting.Endpoint, setting.APIKey);
                var me = await client.CreateUserClient().GetUserMeAsync();
                setting.Configured = true;
                await _settingsRepository.UpdateSetting(setting.ToOptions());
                TempData.SetStatusMessageModel(new StatusMessageModel()
                {
                    Severity = StatusMessageModel.StatusSeverity.Success, Message = "Settings updated."
                });
                return RedirectToAction("UpdateSetting");
            }
            catch (Exception e)
            {
                TempData.SetStatusMessageModel(new StatusMessageModel()
                {
                    Severity = StatusMessageModel.StatusSeverity.Error, Message = "Incorrect setup"
                });
                return View(setting);
            }
        }

        [HttpGet("~/support")]
        [Authorize(AuthenticationSchemes = BTCPayServer.Security.AuthenticationSchemes.Cookie)]
        public async Task<IActionResult> ListTickets()
        {
            var setting = await _settingsRepository.GetSettingAsync<ZammadOptions>();
            if (setting?.Configured is true)
            {
                var client = ZammadAccount.CreateTokenAccount(setting.Endpoint, setting.APIKey);
                var currentUser = _userManager.GetUserId(User);
                var matchedUser = (await client.CreateUserClient().SearchUserAsync(currentUser, 1)).FirstOrDefault();
                if (matchedUser != null)
                {
                    client.UseOnBehalfOf(currentUser);

                    var ticketClient = client.CreateTicketClient();
                    return View(new ListTicketsVM()
                    {
                        States = await ticketClient.GetTicketStateListAsync(),
                        Tickets = (await ticketClient.GetTicketListAsync()).ToList()
                    });
                }
                else
                {
                    return View(new ListTicketsVM() {States = new List<TicketState>(), Tickets = new List<Ticket>()});
                }
            }
            else if (User.IsInRole(Roles.ServerAdmin))
            {
                return RedirectToAction("UpdateSetting");
            }

            return NotFound();
        }

        [HttpGet("~/support/create")]
        [Authorize(AuthenticationSchemes = BTCPayServer.Security.AuthenticationSchemes.Cookie)]
        public async Task<IActionResult> CreateTicket()
        {
            var setting = await _settingsRepository.GetSettingAsync<ZammadOptions>();
            if (setting?.Configured is true)
            {
                return View(new CreateTicketVM());
            }

            return NotFound();
        }

        [HttpPost("~/support/create")]
        [Authorize(AuthenticationSchemes = BTCPayServer.Security.AuthenticationSchemes.Cookie)]
        public async Task<IActionResult> CreateTicket(CreateTicketVM vm)
        {
            var setting = await _settingsRepository.GetSettingAsync<ZammadOptions>();
            if (setting?.Configured is true)
            {
                var client = ZammadAccount.CreateTokenAccount(setting.Endpoint, setting.APIKey);
                var currentUser = _userManager.GetUserId(User);
                var userClient = client.CreateUserClient();
                var adminId = (await userClient.GetUserMeAsync()).Id;
                var matchedUser = (await userClient.SearchUserAsync(currentUser, 1)).FirstOrDefault();
                if (matchedUser == null)
                {
                    var userEmail = _userManager.GetUserName(User);
                    matchedUser = await userClient.CreateUserAsync(new User()
                    {
                        Login = currentUser, Active = true, Verified = true, Email = userEmail
                    });
                }

                if (matchedUser != null)
                {
                    var ticketClient = client.CreateTicketClient();
                    try
                    {
                        Func<Task<int>> GetGroupId = async () =>
                        {
                            if (setting.ServerTicketsGroupId.HasValue)
                            {
                                return setting.ServerTicketsGroupId.Value;
                            }

                            var groupClient = client.CreateGroupClient();
                            return (await groupClient.GetGroupListAsync(1, 1)).FirstOrDefault(group => group.Active)
                                ?.Id ?? 0;
                        };
                        var newTicket = await ticketClient.CreateTicketAsync(
                            new Ticket()
                            {
                                Title = vm.Title,
                                CustomerId = matchedUser.Id,
                                OwnerId = adminId,
                                GroupId = await GetGroupId()
                            },
                            new TicketArticle() {Body = vm.Comment, ContentType = "text/html", Type = "note"});


                        TempData.SetStatusMessageModel(new StatusMessageModel()
                        {
                            Severity = StatusMessageModel.StatusSeverity.Success, Message = "Ticket created"
                        });
                        return RedirectToAction("ViewTicket", new {ticketId = newTicket.Id});
                    }
                    catch (Exception e)
                    {
                        TempData.SetStatusMessageModel(new StatusMessageModel()
                        {
                            Severity = StatusMessageModel.StatusSeverity.Error, Message = e.Message
                        });
                        return View(vm);
                    }
                }
            }

            return NotFound();
        }


        [HttpGet("~/support/{ticketId}")]
        [Authorize(AuthenticationSchemes = BTCPayServer.Security.AuthenticationSchemes.Cookie)]
        public async Task<IActionResult> ViewTicket(int ticketId)
        {
            var setting = await _settingsRepository.GetSettingAsync<ZammadOptions>();
            if (setting?.Configured is true)
            {
                var client = ZammadAccount.CreateTokenAccount(setting.Endpoint, setting.APIKey);
                var currentUser = _userManager.GetUserId(User);
                var matchedUser = (await client.CreateUserClient().SearchUserAsync(currentUser, 1)).FirstOrDefault();
                if (matchedUser != null)
                {
                    var ticketClient = client.CreateTicketClient();
                    var ticket = await ticketClient.GetTicketAsync(ticketId);
                    if (ticket is null || ticket.CustomerId != matchedUser.Id)
                    {
                        return NotFound();
                    }

                    return View(new ViewTicketVM()
                    {
                        ZammadUserId = matchedUser.Id,
                        Ticket = ticket,
                        TicketArticles = await ticketClient.GetTicketArticleListForTicketAsync(ticket.Id),
                        States = await ticketClient.GetTicketStateListAsync(),
                    });
                }
            }

            return NotFound();
        }

        [Authorize(AuthenticationSchemes = BTCPayServer.Security.AuthenticationSchemes.Cookie)]
        [HttpPost("~/support/{ticketId}/reply")]
        public async Task<IActionResult> ReplyToTicket(int ticketId, ReplyTicketVM vm)
        {
            var setting = await _settingsRepository.GetSettingAsync<ZammadOptions>();

            TicketArticle res = null;
            if (setting?.Configured is true)
            {
                var client = ZammadAccount.CreateTokenAccount(setting.Endpoint, setting.APIKey);
                var currentUser = _userManager.GetUserId(User);
                var matchedUser = (await client.CreateUserClient().SearchUserAsync(currentUser, 1)).FirstOrDefault();
                if (matchedUser != null)
                {
                    var ticketClient = client.CreateTicketClient();
                    var ticket = await ticketClient.GetTicketAsync(ticketId);
                    if (ticket is null || ticket.CustomerId != matchedUser.Id)
                    {
                        return NotFound();
                    }

                    try
                    {
                        res = await ticketClient.CreateTicketArticleAsync(new TicketArticle()
                        {
                            Internal = false,
                            TicketId = ticket.Id,
                            OriginById = matchedUser.Id,
                            Body = vm.Comment,
                            ContentType = "text/html"
                        });
                    }
                    catch (Exception e)
                    {
                        TempData.SetStatusMessageModel(new StatusMessageModel()
                        {
                            Severity = StatusMessageModel.StatusSeverity.Error, Message = e.Message
                        });
                    }

                    return RedirectToAction("ViewTicket", "Zammad", new {ticketId},
                        res == null ? null : $"ticketArticle-{res.Id}");
                }
            }

            return NotFound();
        }

        [Authorize(AuthenticationSchemes = BTCPayServer.Security.AuthenticationSchemes.Cookie)]
        [HttpPost("~/support/{ticketId}/resolve")]
        public async Task<IActionResult> ResolveTicket(int ticketId, ReplyTicketVM vm)
        {
            var setting = await _settingsRepository.GetSettingAsync<ZammadOptions>();

            TicketArticle res = null;
            if (setting?.Configured is true)
            {
                var client = ZammadAccount.CreateTokenAccount(setting.Endpoint, setting.APIKey);
                var currentUser = _userManager.GetUserId(User);
                var matchedUser = (await client.CreateUserClient().SearchUserAsync(currentUser, 1)).FirstOrDefault();
                if (matchedUser != null)
                {
                    var ticketClient = client.CreateTicketClient();
                    var ticket = await ticketClient.GetTicketAsync(ticketId);
                    if (ticket is null || ticket.CustomerId != matchedUser.Id)
                    {
                        return NotFound();
                    }

                    try
                    {
                        var state = (await ticketClient.GetTicketStateListAsync()).FirstOrDefault(ticketState =>
                            ticketState.Active && ticketState.Name == "closed");
                        if (state != null)
                        {
                            ticket.StateId = state.Id;
                            ticket.UpdatedById = matchedUser.Id;
                            await ticketClient.UpdateTicketAsync(ticket.Id, ticket);
                        }
                    }
                    catch (Exception e)
                    {
                        TempData.SetStatusMessageModel(new StatusMessageModel()
                        {
                            Severity = StatusMessageModel.StatusSeverity.Error, Message = e.Message
                        });
                    }

                    return RedirectToAction("ViewTicket", "Zammad", new {ticketId});
                }
            }

            return NotFound();
        }
    }
}
